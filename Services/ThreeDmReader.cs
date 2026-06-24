using Rhino.FileIO;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;

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
        var layerNameByIndex = file.AllLayers.ToDictionary(layer => layer.Index, layer => FirstNonEmpty(layer.FullPath, layer.Name, $"Layer {layer.Index}"));

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
                ExpandInstanceReference(definitionById, objectById, layerNameByIndex, fileObject, instanceReference, Transform.Identity, result, 0);
                continue;
            }

            result.Add(CreateBimObject(layerNameByIndex, fileObject, fileObject.Attributes, fileObject.Geometry, Transform.Identity, null));
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
        IReadOnlyDictionary<int, string> layerNameByIndex,
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
                ExpandInstanceReference(definitionById, objectById, layerNameByIndex, partObject, nestedReference, composedTransform, result, depth + 1);
                continue;
            }

            result.Add(CreateBimObject(layerNameByIndex, partObject, MergeAttributes(instanceObject.Attributes, partObject.Attributes), partObject.Geometry, composedTransform, instanceObject));
        }
    }

    private static RhinoBimObject CreateBimObject(
        IReadOnlyDictionary<int, string> layerNameByIndex,
        File3dmObject fileObject,
        ObjectAttributes attributes,
        GeometryBase geometry,
        Transform transform,
        File3dmObject? instanceObject)
    {
        var definitionLayerName = ResolveLayerName(layerNameByIndex, attributes.LayerIndex);
        var instanceLayerName = instanceObject?.Attributes is null ? string.Empty : ResolveLayerName(layerNameByIndex, instanceObject.Attributes.LayerIndex);
        var layerName = FirstNonEmpty(instanceLayerName, definitionLayerName);
        var name = FirstNonEmpty(attributes.Name, fileObject.Name, instanceObject?.Name, fileObject.Id.ToString());
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
            Geometry = copiedGeometry
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

    private static string ResolveLayerName(IReadOnlyDictionary<int, string> layerNameByIndex, int layerIndex)
    {
        if (layerIndex >= 0 && layerNameByIndex.TryGetValue(layerIndex, out var layerName))
        {
            return layerName;
        }

        return string.Empty;
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
}
