using Newtonsoft.Json.Linq;
using Rhino3dmToIfc.Console.Models;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.UtilityResource;

namespace Rhino3dmToIfc.Console.Services;

public sealed class IfcPropertySetBuilder
{
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
            ["SourceGeometryType"] = source.GeometryType,
            ["ExportedBy"] = "Rhino3dmToIfc.Console",
            ["ExportedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };

        AttachPropertySet(product, "Pset_RhinoSource", sourceProperties);

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
