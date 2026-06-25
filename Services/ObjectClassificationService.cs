using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;
using Xbim.Ifc4.UtilityResource;

namespace Rhino3dmToIfc.Console.Services;

public sealed class ObjectClassificationService
{
    private static readonly string[] SupportedIfcTypes =
    [
        "IfcWall",
        "IfcSlab",
        "IfcColumn",
        "IfcBeam",
        "IfcDoor",
        "IfcWindow",
        "IfcRoof",
        "IfcStair",
        "IfcRailing",
        "IfcCovering",
        "IfcMember",
        "IfcBuildingElementProxy"
    ];

    private readonly LogService _log;

    public ObjectClassificationService(LogService log)
    {
        _log = log;
    }

    public IReadOnlyList<RhinoBimObject> Classify(IEnumerable<RhinoBimObject> objects, IfcExportOptions options, ExportSummary summary)
    {
        var result = new List<RhinoBimObject>();

        foreach (var obj in objects)
        {
            var classification = ResolveClassification(obj, options.DefaultIfcType);
            obj.IfcType = classification.IfcType;
            obj.IfcPredefinedType = GetUserText(obj, "IfcPredefinedType", classification.PredefinedType);
            obj.IfcObjectType = GetUserText(obj, "IfcObjectType", classification.ObjectType);
            obj.IfcGlobalId = ResolveGlobalId(obj);
            obj.IfcName = GetUserText(obj, "IfcName", GetFirstUserText(obj, obj.ObjectName, "Panel_Name", "Panel Name", "Part_ID", "Part ID"));
            obj.IfcDescription = GetUserText(obj, "IfcDescription", string.Empty);
            obj.IfcStorey = GetUserText(obj, "IfcStorey", options.DefaultStorey);
            obj.IfcMaterial = GetUserText(obj, "IfcMaterial", GetFirstUserText(obj, obj.RhinoMaterialName, "Material"));
            obj.IfcPropertySetsJson = GetUserText(obj, "IfcPropertySetsJson", string.Empty);
            obj.IfcFullDataJson = GetUserText(obj, "IfcFullDataJson", string.Empty);
            obj.IsSupportedGeometry = obj.Geometry is Mesh or Brep or Extrusion or Surface or Curve;

            if (obj.Geometry is Mesh)
            {
                summary.MeshObjects++;
            }
            else if (obj.Geometry is Brep or Extrusion or Surface or Curve)
            {
                summary.MeshObjects++;
            }
            else
            {
                obj.SkipReason = UnsupportedReason(obj.GeometryType);
                IncrementUnsupported(summary, obj.GeometryType);
                _log.Warning($"Skipping {obj.RhinoObjectId}: {obj.SkipReason}");
                summary.WarningCount++;
            }

            result.Add(obj);
        }

        return result;
    }

    private static Classification ResolveClassification(RhinoBimObject obj, string defaultIfcType)
    {
        var fromLayerMapping = ResolveLayerClassification(obj);
        var fromUserText = NormalizeIfcType(GetUserText(obj, "IfcType", string.Empty));
        if (!string.IsNullOrEmpty(fromUserText))
        {
            return string.Equals(fromUserText, fromLayerMapping.IfcType, StringComparison.OrdinalIgnoreCase)
                ? new Classification(fromUserText, fromLayerMapping.PredefinedType, fromLayerMapping.ObjectType)
                : new Classification(fromUserText, string.Empty, string.Empty);
        }

        if (!string.IsNullOrEmpty(fromLayerMapping.IfcType))
        {
            return fromLayerMapping;
        }

        var fromLayer = FindSupportedIfcType(GetLayerSearchText(obj));
        if (!string.IsNullOrEmpty(fromLayer))
        {
            return new Classification(fromLayer, string.Empty, string.Empty);
        }

        var fromName = ResolveNamePrefix(obj.ObjectName);
        if (!string.IsNullOrEmpty(fromName))
        {
            return new Classification(fromName, string.Empty, string.Empty);
        }

        var fallback = NormalizeIfcType(defaultIfcType) is { Length: > 0 } normalizedDefault
            ? normalizedDefault
            : "IfcBuildingElementProxy";
        return new Classification(fallback, string.Empty, string.Empty);
    }

    private static Classification ResolveLayerClassification(RhinoBimObject obj)
    {
        var layerText = GetLayerSearchText(obj);
        if (ContainsAny(layerText, "stiffener", "stiffeners"))
        {
            return new Classification("IfcMember", "USERDEFINED", "Stiffener");
        }

        if (ContainsAny(layerText, "angle", "angles", "bracket", "brackets", "fixing"))
        {
            return new Classification("IfcMember", "USERDEFINED", "L-Angle");
        }

        if (ContainsAny(layerText, "panel", "panels", "cladding", "clad", "sheet", "sheets"))
        {
            return new Classification("IfcCovering", "CLADDING", "Cladding Sheet");
        }

        return Classification.Empty;
    }

    private static string GetLayerSearchText(RhinoBimObject obj)
    {
        return string.Join(" ", obj.LayerName, string.Join(" ", obj.AdditionalLayerNames));
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveGlobalId(RhinoBimObject obj)
    {
        var provided = GetUserText(obj, "IfcGlobalId", string.Empty);
        if (IsValidCompressedIfcGlobalId(provided))
        {
            return provided;
        }

        if (!string.IsNullOrWhiteSpace(provided))
        {
            _log.Warning($"Object {obj.RhinoObjectId} has invalid IfcGlobalId '{provided}'. A new GlobalId was generated.");
        }

        return IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
    }

    private static string GetUserText(RhinoBimObject obj, string key, string fallback)
    {
        return obj.UserText.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static string GetFirstUserText(RhinoBimObject obj, string fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.UserText.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback;
    }

    private static string NormalizeIfcType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("IfcWallStandardCase", StringComparison.OrdinalIgnoreCase))
        {
            return "IfcWall";
        }

        return SupportedIfcTypes.FirstOrDefault(type => type.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string FindSupportedIfcType(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return SupportedIfcTypes.FirstOrDefault(type => text.Contains(type, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string ResolveNamePrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var prefix = name.Split(['_', '-', ' ', ':', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return NormalizeIfcType(prefix);
    }

    private static bool IsValidCompressedIfcGlobalId(string value)
    {
        return value.Length == 22 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
    }

    private static string UnsupportedReason(string geometryType)
    {
        return geometryType switch
        {
            "Brep" => "Brep geometry could not be converted to an exportable tessellation.",
            "Surface" => "Surface geometry could not be converted to an exportable tessellation.",
            "Extrusion" => "Extrusion geometry could not be converted to an exportable tessellation.",
            "Curve" => "Curve geometry could not be converted to an exportable IFC polyline.",
            _ => $"{geometryType} geometry is not supported in Version 1."
        };
    }

    private static void IncrementUnsupported(ExportSummary summary, string geometryType)
    {
        switch (geometryType)
        {
            case "Brep":
                summary.UnsupportedBrepObjects++;
                break;
            case "Surface":
                summary.UnsupportedSurfaceObjects++;
                break;
            case "Extrusion":
                summary.UnsupportedExtrusionObjects++;
                break;
            default:
                summary.UnsupportedOtherObjects++;
                break;
        }
    }

    private sealed record Classification(string IfcType, string PredefinedType, string ObjectType)
    {
        public static Classification Empty { get; } = new(string.Empty, string.Empty, string.Empty);
    }
}
