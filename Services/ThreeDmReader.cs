using Rhino.FileIO;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;
using System.Drawing;

namespace Rhino3dmToIfc.Console.Services;

public sealed class ThreeDmReader
{
    private readonly LogService _log;

    public IReadOnlyList<RhinoLayerInfo> LastReadLayers { get; private set; } = [];

    public ThreeDmReader(LogService log)
    {
        _log = log;
    }

    public List<RhinoBimObject> Read(string inputPath)
    {
        var file = File3dm.Read(inputPath) ?? throw new InvalidOperationException($"Unable to read 3DM file: {inputPath}");
        LastReadLayers = ReadLayers(file);
        var result = new List<RhinoBimObject>();
        var objectById = file.Objects
            .Where(fileObject => fileObject.HasId)
            .ToDictionary(fileObject => fileObject.Id);
        var definitionById = file.AllInstanceDefinitions.ToDictionary(definition => definition.Id);
        var layerByIndex = file.AllLayers.ToDictionary(layer => layer.Index);
        var materialByIndex = file.AllMaterials
            .Where(material => material.MaterialIndex >= 0)
            .ToDictionary(material => material.MaterialIndex);

        foreach (var fileObject in file.Objects)
        {
            if (fileObject.IsDeleted || fileObject.Geometry is null || fileObject.Attributes is null)
            {
                continue;
            }

            if (fileObject.Attributes.IsInstanceDefinitionObject)
            {
                continue;
            }

            if (fileObject.Geometry is InstanceReferenceGeometry instanceReference)
            {
                ExpandInstanceReference(definitionById, objectById, layerByIndex, materialByIndex, fileObject, instanceReference, Transform.Identity, result, 0);
                continue;
            }

            result.Add(CreateBimObject(layerByIndex, materialByIndex, fileObject, fileObject.Attributes, fileObject.Geometry, Transform.Identity, null));
        }

        _log.Info($"Read {result.Count} non-deleted objects from 3DM.");
        _log.Info($"Read {LastReadLayers.Count} Rhino layers from 3DM.");
        return result;
    }

    private static IReadOnlyList<RhinoLayerInfo> ReadLayers(File3dm file)
    {
        return file.AllLayers
            .Select(layer => FirstNonEmpty(layer.FullPath, layer.Name))
            .Where(layerName => !string.IsNullOrWhiteSpace(layerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(layerName => new RhinoLayerInfo { Name = layerName })
            .ToList();
    }

    private void ExpandInstanceReference(
        IReadOnlyDictionary<Guid, InstanceDefinitionGeometry> definitionById,
        IReadOnlyDictionary<Guid, File3dmObject> objectById,
        IReadOnlyDictionary<int, Layer> layerByIndex,
        IReadOnlyDictionary<int, Material> materialByIndex,
        File3dmObject instanceObject,
        InstanceReferenceGeometry instanceReference,
        Transform parentTransform,
        List<RhinoBimObject> result,
        int depth)
    {
        if (depth > 16)
        {
            _log.Warning($"Skipping nested instance {instanceObject.Id}: maximum instance nesting depth reached.");
            return;
        }

        if (!definitionById.TryGetValue(instanceReference.ParentIdefId, out var definition))
        {
            _log.Warning($"Skipping instance {instanceObject.Id}: instance definition {instanceReference.ParentIdefId} was not found.");
            return;
        }

        var composedTransform = parentTransform * instanceReference.Xform;
        foreach (var partId in definition.GetObjectIds())
        {
            if (!objectById.TryGetValue(partId, out var partObject) || partObject.Geometry is null || partObject.Attributes is null || partObject.IsDeleted)
            {
                continue;
            }

            if (partObject.Geometry is InstanceReferenceGeometry nestedReference)
            {
                ExpandInstanceReference(definitionById, objectById, layerByIndex, materialByIndex, partObject, nestedReference, composedTransform, result, depth + 1);
                continue;
            }

            result.Add(CreateBimObject(layerByIndex, materialByIndex, partObject, MergeAttributes(instanceObject.Attributes, partObject.Attributes), partObject.Geometry, composedTransform, instanceObject));
        }
    }

    private static RhinoBimObject CreateBimObject(
        IReadOnlyDictionary<int, Layer> layerByIndex,
        IReadOnlyDictionary<int, Material> materialByIndex,
        File3dmObject fileObject,
        ObjectAttributes attributes,
        GeometryBase geometry,
        Transform transform,
        File3dmObject? instanceObject)
    {
        var definitionLayerName = ResolveLayerName(layerByIndex, attributes.LayerIndex);
        var instanceLayerName = instanceObject?.Attributes is null ? string.Empty : ResolveLayerName(layerByIndex, instanceObject.Attributes.LayerIndex);
        var layerName = FirstNonEmpty(instanceLayerName, definitionLayerName);
        var name = FirstNonEmpty(attributes.Name, fileObject.Name, instanceObject?.Name, fileObject.Id.ToString());
        var resolvedMaterial = ResolveMaterial(attributes, instanceObject?.Attributes, layerByIndex, materialByIndex);
        var resolvedColor = ResolveDisplayColor(attributes, instanceObject?.Attributes, layerByIndex, materialByIndex, resolvedMaterial);
        var copiedGeometry = geometry.Duplicate();
        if (!transform.IsIdentity)
        {
            copiedGeometry.Transform(transform);
        }

        var userText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (instanceObject?.Attributes is not null)
        {
            AddUserStrings(userText, instanceObject.Attributes.GetUserStrings());
        }

        AddUserStrings(userText, attributes.GetUserStrings());
        AddUserStrings(userText, copiedGeometry.GetUserStrings());

        return new RhinoBimObject
        {
            RhinoObjectId = instanceObject?.Id ?? fileObject.Id,
            ObjectName = name,
            LayerName = layerName,
            AdditionalLayerNames = GetAdditionalLayerNames(layerName, definitionLayerName),
            GeometryType = copiedGeometry.GetType().Name,
            UserText = userText,
            Geometry = copiedGeometry,
            RhinoMaterialName = resolvedMaterial?.Name ?? string.Empty,
            DisplayColorName = resolvedColor.Name,
            DisplayColorRed = resolvedColor.Color?.R,
            DisplayColorGreen = resolvedColor.Color?.G,
            DisplayColorBlue = resolvedColor.Color?.B,
            DisplayTransparency = resolvedColor.Transparency
        };
    }

    private static List<string> GetAdditionalLayerNames(string primaryLayerName, string definitionLayerName)
    {
        if (string.IsNullOrWhiteSpace(definitionLayerName) ||
            string.Equals(primaryLayerName, definitionLayerName, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [definitionLayerName];
    }

    private static ObjectAttributes MergeAttributes(ObjectAttributes instanceAttributes, ObjectAttributes partAttributes)
    {
        var merged = partAttributes.Duplicate();
        if (string.IsNullOrWhiteSpace(merged.Name))
        {
            merged.Name = instanceAttributes.Name;
        }

        if (merged.LayerIndex < 0)
        {
            merged.LayerIndex = instanceAttributes.LayerIndex;
        }

        foreach (var key in instanceAttributes.GetUserStrings()?.AllKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(merged.GetUserString(key)))
            {
                merged.SetUserString(key, instanceAttributes.GetUserString(key));
            }
        }

        return merged;
    }

    private static string ResolveLayerName(IReadOnlyDictionary<int, Layer> layerByIndex, int layerIndex)
    {
        if (layerIndex >= 0 && layerByIndex.TryGetValue(layerIndex, out var layer))
        {
            return FirstNonEmpty(layer.FullPath, layer.Name, $"Layer {layer.Index}");
        }

        return string.Empty;
    }

    private static Material? ResolveMaterial(
        ObjectAttributes attributes,
        ObjectAttributes? parentAttributes,
        IReadOnlyDictionary<int, Layer> layerByIndex,
        IReadOnlyDictionary<int, Material> materialByIndex)
    {
        return attributes.MaterialSource switch
        {
            ObjectMaterialSource.MaterialFromObject => ResolveMaterialByIndex(attributes.MaterialIndex, materialByIndex),
            ObjectMaterialSource.MaterialFromLayer => ResolveLayerMaterial(attributes.LayerIndex, layerByIndex, materialByIndex),
            ObjectMaterialSource.MaterialFromParent when parentAttributes is not null => ResolveMaterial(parentAttributes, null, layerByIndex, materialByIndex),
            _ => ResolveMaterialByIndex(attributes.MaterialIndex, materialByIndex) ?? ResolveLayerMaterial(attributes.LayerIndex, layerByIndex, materialByIndex)
        };
    }

    private static Material? ResolveMaterialByIndex(int materialIndex, IReadOnlyDictionary<int, Material> materialByIndex)
    {
        return materialIndex >= 0 && materialByIndex.TryGetValue(materialIndex, out var material) && !material.IsDeleted
            ? material
            : null;
    }

    private static Material? ResolveLayerMaterial(
        int layerIndex,
        IReadOnlyDictionary<int, Layer> layerByIndex,
        IReadOnlyDictionary<int, Material> materialByIndex)
    {
        return layerIndex >= 0 && layerByIndex.TryGetValue(layerIndex, out var layer)
            ? ResolveMaterialByIndex(layer.RenderMaterialIndex, materialByIndex)
            : null;
    }

    private static ResolvedColor ResolveDisplayColor(
        ObjectAttributes attributes,
        ObjectAttributes? parentAttributes,
        IReadOnlyDictionary<int, Layer> layerByIndex,
        IReadOnlyDictionary<int, Material> materialByIndex,
        Material? resolvedMaterial)
    {
        return attributes.ColorSource switch
        {
            ObjectColorSource.ColorFromObject => FromColor(attributes.ObjectColor, "Rhino object color"),
            ObjectColorSource.ColorFromLayer => FromLayer(attributes.LayerIndex, layerByIndex),
            ObjectColorSource.ColorFromMaterial => FromMaterial(resolvedMaterial ?? ResolveMaterial(attributes, parentAttributes, layerByIndex, materialByIndex)),
            ObjectColorSource.ColorFromParent when parentAttributes is not null => ResolveDisplayColor(parentAttributes, null, layerByIndex, materialByIndex, ResolveMaterial(parentAttributes, null, layerByIndex, materialByIndex)),
            _ => FromColor(attributes.ObjectColor, "Rhino object color")
        };
    }

    private static ResolvedColor FromLayer(int layerIndex, IReadOnlyDictionary<int, Layer> layerByIndex)
    {
        return layerIndex >= 0 && layerByIndex.TryGetValue(layerIndex, out var layer)
            ? FromColor(layer.Color, $"Rhino layer color: {FirstNonEmpty(layer.FullPath, layer.Name, $"Layer {layer.Index}")}")
            : ResolvedColor.Empty;
    }

    private static ResolvedColor FromMaterial(Material? material)
    {
        if (material is null)
        {
            return ResolvedColor.Empty;
        }

        var color = material.DiffuseColor.IsEmpty ? material.PreviewColor : material.DiffuseColor;
        return new ResolvedColor(
            color.IsEmpty ? null : color,
            FirstNonEmpty(material.Name, "Rhino material color"),
            Clamp01(material.Transparency));
    }

    private static ResolvedColor FromColor(Color color, string name)
    {
        return color.IsEmpty
            ? ResolvedColor.Empty
            : new ResolvedColor(color, name, color.A < 255 ? Clamp01(1.0 - (color.A / 255.0)) : null);
    }

    private static void AddUserStrings(Dictionary<string, string> values, System.Collections.Specialized.NameValueCollection? userStrings)
    {
        if (userStrings is null)
        {
            return;
        }

        foreach (var key in userStrings.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = userStrings[key];
            if (value is not null)
            {
                values[key] = value;
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static double Clamp01(double value)
    {
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    private sealed record ResolvedColor(Color? Color, string Name, double? Transparency)
    {
        public static ResolvedColor Empty { get; } = new(null, string.Empty, null);
    }
}
