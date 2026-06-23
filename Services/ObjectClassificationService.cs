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
            obj.IfcType = ResolveIfcType(obj, options.DefaultIfcType);
            obj.IfcGlobalId = ResolveGlobalId(obj);
            obj.IfcName = GetUserText(obj, "IfcName", obj.ObjectName);
            obj.IfcDescription = GetUserText(obj, "IfcDescription", string.Empty);
            obj.IfcStorey = GetUserText(obj, "IfcStorey", options.DefaultStorey);
            obj.IfcMaterial = GetUserText(obj, "IfcMaterial", string.Empty);
            obj.IfcPropertySetsJson = GetUserText(obj, "IfcPropertySetsJson", string.Empty);
            obj.IfcFullDataJson = GetUserText(obj, "IfcFullDataJson", string.Empty);
            obj.IsSupportedGeometry = obj.Geometry is Mesh or Brep or Extrusion or LineCurve;

            if (obj.Geometry is Mesh)
            {
                summary.MeshObjects++;
            }
            else if (obj.Geometry is Brep or Extrusion or LineCurve)
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

    private static string ResolveIfcType(RhinoBimObject obj, string defaultIfcType)
    {
        var fromUserText = NormalizeIfcType(GetUserText(obj, "IfcType", string.Empty));
        if (!string.IsNullOrEmpty(fromUserText))
        {
            return fromUserText;
        }

        var fromLayer = FindSupportedIfcType(obj.LayerName);
        if (!string.IsNullOrEmpty(fromLayer))
        {
            return fromLayer;
        }

        var fromName = ResolveNamePrefix(obj.ObjectName);
        if (!string.IsNullOrEmpty(fromName))
        {
            return fromName;
        }

        return NormalizeIfcType(defaultIfcType) is { Length: > 0 } normalizedDefault
            ? normalizedDefault
            : "IfcBuildingElementProxy";
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
            "Brep" => "Brep geometry is not supported in Version 1. Brep meshing requires Rhino or Rhino Compute in Version 2.",
            "Surface" => "Surface geometry is not supported in Version 1. Surface meshing requires Rhino or Rhino Compute in Version 2.",
            "Extrusion" => "Extrusion geometry is not supported in Version 1. Extrusion meshing requires Rhino or Rhino Compute in Version 2.",
            "Curve" => "Curve geometry is not supported in Version 1.",
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
}
