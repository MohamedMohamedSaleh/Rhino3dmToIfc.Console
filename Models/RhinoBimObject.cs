namespace Rhino3dmToIfc.Console.Models;

public sealed class RhinoBimObject
{
    public Guid RhinoObjectId { get; set; }

    public string ObjectName { get; set; } = string.Empty;

    public string LayerName { get; set; } = string.Empty;

    public List<string> AdditionalLayerNames { get; set; } = [];

    public string GeometryType { get; set; } = string.Empty;

    public Dictionary<string, string> UserText { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public object? Geometry { get; set; }

    public string RhinoMaterialName { get; set; } = string.Empty;

    public string DisplayColorName { get; set; } = string.Empty;

    public byte? DisplayColorRed { get; set; }

    public byte? DisplayColorGreen { get; set; }

    public byte? DisplayColorBlue { get; set; }

    public double? DisplayTransparency { get; set; }

    public string IfcGlobalId { get; set; } = string.Empty;

    public string IfcType { get; set; } = string.Empty;

    public string IfcPredefinedType { get; set; } = string.Empty;

    public string IfcObjectType { get; set; } = string.Empty;

    public string IfcName { get; set; } = string.Empty;

    public string IfcDescription { get; set; } = string.Empty;

    public string IfcStorey { get; set; } = string.Empty;

    public string IfcMaterial { get; set; } = string.Empty;

    public string IfcPropertySetsJson { get; set; } = string.Empty;

    public string IfcFullDataJson { get; set; } = string.Empty;

    public bool IsSupportedGeometry { get; set; }

    public string SkipReason { get; set; } = string.Empty;
}
