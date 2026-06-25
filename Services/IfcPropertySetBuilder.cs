using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.QuantityResource;
using Xbim.Ifc4.UtilityResource;

namespace Rhino3dmToIfc.Console.Services;

public sealed class IfcPropertySetBuilder
{
    private static readonly HashSet<string> InternalUserTextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "IfcPropertySetsJson",
        "IfcFullDataJson"
    };

    private readonly LogService _log;

    public IfcPropertySetBuilder(LogService log)
    {
        _log = log;
    }

    public void AttachPropertySets(IfcProduct product, RhinoBimObject source, ExportSummary summary)
    {
        var model = product.Model;
        var sourceProperties = new Dictionary<string, object?>
        {
            ["SourceRhinoObjectId"] = source.RhinoObjectId.ToString(),
            ["SourceLayer"] = source.LayerName,
            ["SourceAdditionalLayers"] = string.Join("; ", source.AdditionalLayerNames),
            ["SourceGeometryType"] = source.GeometryType,
            ["ExportedBy"] = "Rhino3dmToIfc.Console",
            ["ExportedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        AddGeometryProperties(sourceProperties, source.Geometry);

        AttachPropertySet(product, "Pset_RhinoSource", sourceProperties);
        AttachRhinoUserText(product, source);
        AttachFabricationProperties(product, source);
        AttachFabricationQuantities(product, source);

        if (string.IsNullOrWhiteSpace(source.IfcPropertySetsJson))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(source.IfcPropertySetsJson);
            foreach (var propertySet in root.Properties())
            {
                if (propertySet.Value is not JObject propertyObject)
                {
                    _log.Warning($"Ignoring custom property set '{propertySet.Name}' on {source.RhinoObjectId}; expected an object.");
                    summary.WarningCount++;
                    continue;
                }

                var values = propertyObject.Properties()
                    .Where(property => IsSupportedValue(property.Value))
                    .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value), StringComparer.OrdinalIgnoreCase);

                if (values.Count > 0)
                {
                    AttachPropertySet(product, propertySet.Name, values);
                }
            }
        }
        catch (Exception ex)
        {
            summary.WarningCount++;
            _log.Warning($"Invalid IfcPropertySetsJson on {source.RhinoObjectId}. Custom properties were skipped. {ex.Message}");
        }
    }

    private static void AttachRhinoUserText(IfcProduct product, RhinoBimObject source)
    {
        var values = source.UserText
            .Where(pair => !InternalUserTextKeys.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (values.Count > 0)
        {
            AttachPropertySet(product, "Pset_RhinoUserText", values);
        }
    }

    private static void AttachFabricationProperties(IfcProduct product, RhinoBimObject source)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddTextIfPresent(values, "PanelName", source, "Panel_Name", "Panel Name");
        AddTextIfPresent(values, "Material", source, "Material");
        AddNumberIfPresent(values, "Thickness", source, "Thickness");
        AddNumberIfPresent(values, "Width", source, "Width");
        AddNumberIfPresent(values, "Length", source, "Length");
        AddNumberIfPresent(values, "Depth", source, "Depth");
        AddNumberIfPresent(values, "Quantity", source, "Qty", "Quantity", "Count");
        AddTextIfPresent(values, "Installation", source, "Installation");
        AddTextIfPresent(values, "Screw", source, "Screw");
        AddTextIfPresent(values, "Tape", source, "Tape");
        AddTextIfPresent(values, "PopRivet", source, "pop rivet", "PopRivet", "Pop Rivet");
        AddTextIfPresent(values, "GeoType", source, "Geo Type", "GeoType");
        AddTextIfPresent(values, "PanelPlane", source, "Panel_Plane", "Panel Plane");
        AddTextIfPresent(values, "PancakeGenerated", source, "_PancakeGenerated");

        if (values.Count > 0)
        {
            AttachPropertySet(product, "Pset_Fabrication", values);
        }
    }

    private static void AttachFabricationQuantities(IfcProduct product, RhinoBimObject source)
    {
        var quantities = new List<IfcPhysicalQuantity>();
        AddLengthQuantity(product, quantities, source, "Thickness", "Thickness");
        AddLengthQuantity(product, quantities, source, "Width", "Width");
        AddLengthQuantity(product, quantities, source, "Length", "Length");
        AddLengthQuantity(product, quantities, source, "Depth", "Depth");
        AddCountQuantity(product, quantities, source, "Quantity", "Qty", "Quantity", "Count");

        if (TryGetNumber(source, out var length, "Length") && TryGetNumber(source, out var width, "Width"))
        {
            var area = product.Model.Instances.New<IfcQuantityArea>();
            area.Name = "GrossArea";
            area.AreaValue = new IfcAreaMeasure(length * width);
            area.Formula = "Length * Width";
            quantities.Add(area);

            if (TryGetNumber(source, out var thickness, "Thickness"))
            {
                var volume = product.Model.Instances.New<IfcQuantityVolume>();
                volume.Name = "GrossVolume";
                volume.VolumeValue = new IfcVolumeMeasure(length * width * thickness);
                volume.Formula = "Length * Width * Thickness";
                quantities.Add(volume);
            }
        }

        if (quantities.Count > 0)
        {
            AttachQuantitySet(product, "Qto_FabricationBaseQuantities", quantities);
        }
    }

    private static void AddTextIfPresent(Dictionary<string, object?> values, string outputName, RhinoBimObject source, params string[] sourceKeys)
    {
        if (TryGetUserText(source, out var value, sourceKeys))
        {
            values[outputName] = value;
        }
    }

    private static void AddNumberIfPresent(Dictionary<string, object?> values, string outputName, RhinoBimObject source, params string[] sourceKeys)
    {
        if (TryGetNumber(source, out var value, sourceKeys))
        {
            values[outputName] = value;
        }
    }

    private static void AddLengthQuantity(IfcProduct product, List<IfcPhysicalQuantity> quantities, RhinoBimObject source, string quantityName, params string[] sourceKeys)
    {
        if (!TryGetNumber(source, out var value, sourceKeys))
        {
            return;
        }

        var quantity = product.Model.Instances.New<IfcQuantityLength>();
        quantity.Name = quantityName;
        quantity.LengthValue = new IfcLengthMeasure(value);
        quantities.Add(quantity);
    }

    private static void AddCountQuantity(IfcProduct product, List<IfcPhysicalQuantity> quantities, RhinoBimObject source, string quantityName, params string[] sourceKeys)
    {
        if (!TryGetNumber(source, out var value, sourceKeys))
        {
            return;
        }

        var quantity = product.Model.Instances.New<IfcQuantityCount>();
        quantity.Name = quantityName;
        quantity.CountValue = new IfcCountMeasure(value);
        quantities.Add(quantity);
    }

    private static void AttachPropertySet(IfcProduct product, string propertySetName, IReadOnlyDictionary<string, object?> values)
    {
        var model = product.Model;
        var propertySet = model.Instances.New<IfcPropertySet>();
        propertySet.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        propertySet.Name = propertySetName;

        foreach (var (name, value) in values)
        {
            var property = model.Instances.New<IfcPropertySingleValue>();
            property.Name = name;
            property.NominalValue = ToIfcValue(value);
            propertySet.HasProperties.Add(property);
        }

        var relation = model.Instances.New<IfcRelDefinesByProperties>();
        relation.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        relation.RelatingPropertyDefinition = propertySet;
        relation.RelatedObjects.Add(product);
    }

    private static void AttachQuantitySet(IfcProduct product, string quantitySetName, IReadOnlyCollection<IfcPhysicalQuantity> quantities)
    {
        var model = product.Model;
        var quantitySet = model.Instances.New<IfcElementQuantity>();
        quantitySet.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        quantitySet.Name = quantitySetName;
        quantitySet.MethodOfMeasurement = "Rhino user text";

        foreach (var quantity in quantities)
        {
            quantitySet.Quantities.Add(quantity);
        }

        var relation = model.Instances.New<IfcRelDefinesByProperties>();
        relation.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        relation.RelatingPropertyDefinition = quantitySet;
        relation.RelatedObjects.Add(product);
    }

    private static void AddGeometryProperties(Dictionary<string, object?> target, object? geometry)
    {
        if (geometry is not GeometryBase geometryBase)
        {
            return;
        }

        var bounds = geometryBase.GetBoundingBox(true);
        if (bounds.IsValid)
        {
            target["BoundingBoxMinX"] = bounds.Min.X;
            target["BoundingBoxMinY"] = bounds.Min.Y;
            target["BoundingBoxMinZ"] = bounds.Min.Z;
            target["BoundingBoxMaxX"] = bounds.Max.X;
            target["BoundingBoxMaxY"] = bounds.Max.Y;
            target["BoundingBoxMaxZ"] = bounds.Max.Z;
            target["ApproxWidthX"] = bounds.Max.X - bounds.Min.X;
            target["ApproxDepthY"] = bounds.Max.Y - bounds.Min.Y;
            target["ApproxHeightZ"] = bounds.Max.Z - bounds.Min.Z;
        }

        switch (geometry)
        {
            case Mesh mesh:
                target["MeshVertexCount"] = mesh.Vertices.Count;
                target["MeshFaceCount"] = mesh.Faces.Count;
                target["MeshIsClosed"] = mesh.IsClosed;
                break;
            case Brep brep:
                target["BrepFaceCount"] = brep.Faces.Count;
                target["BrepEdgeCount"] = brep.Edges.Count;
                target["BrepIsSolid"] = brep.IsSolid;
                break;
            case Extrusion extrusion:
                target["ExtrusionIsSolid"] = extrusion.IsSolid;
                break;
            case Curve curve:
                target["CurveIsClosed"] = curve.IsClosed;
                break;
            case Surface surface:
                target["SurfaceIsClosedU"] = surface.IsClosed(0);
                target["SurfaceIsClosedV"] = surface.IsClosed(1);
                break;
        }
    }

    private static bool IsSupportedValue(JToken token)
    {
        return token.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean;
    }

    private static object? ConvertJsonValue(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            _ => token.Value<string>()
        };
    }

    private static bool TryGetUserText(RhinoBimObject source, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (source.UserText.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetNumber(RhinoBimObject source, out double value, params string[] keys)
    {
        value = 0;
        return TryGetUserText(source, out var text, keys) && TryParseNumber(text, out value);
    }

    private static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var normalized = trimmed.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var match = Regex.Match(normalized, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IfcValue ToIfcValue(object? value)
    {
        return value switch
        {
            bool boolValue => new IfcBoolean(boolValue),
            int intValue => new IfcInteger(intValue),
            long longValue when longValue is <= int.MaxValue and >= int.MinValue => new IfcInteger((int)longValue),
            float floatValue => new IfcReal(floatValue),
            double doubleValue => new IfcReal(doubleValue),
            decimal decimalValue => new IfcReal((double)decimalValue),
            _ => new IfcText(value?.ToString() ?? string.Empty)
        };
    }
}
