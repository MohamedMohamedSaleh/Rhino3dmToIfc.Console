using Rhino.FileIO;
using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;

namespace Rhino3dmToIfc.Console.Services;

public sealed class ThreeDmReader
{
    private readonly LogService _log;

    public ThreeDmReader(LogService log)
    {
        _log = log;
    }

    public List<RhinoBimObject> Read(string inputPath)
    {
        var file = File3dm.Read(inputPath) ?? throw new InvalidOperationException($"Unable to read 3DM file: {inputPath}");
        var result = new List<RhinoBimObject>();

        foreach (var fileObject in file.Objects)
        {
            if (fileObject.IsDeleted || fileObject.Geometry is null || fileObject.Attributes is null)
            {
                continue;
            }

            var attributes = fileObject.Attributes;
            var layerName = ResolveLayerName(file, attributes.LayerIndex);
            var name = FirstNonEmpty(attributes.Name, fileObject.Name, fileObject.Id.ToString());

            result.Add(new RhinoBimObject
            {
                RhinoObjectId = fileObject.Id,
                ObjectName = name,
                LayerName = layerName,
                GeometryType = fileObject.Geometry.GetType().Name,
                UserText = ReadUserText(attributes, fileObject.Geometry),
                Geometry = fileObject.Geometry
            });
        }

        _log.Info($"Read {result.Count} non-deleted objects from 3DM.");
        return result;
    }

    private static string ResolveLayerName(File3dm file, int layerIndex)
    {
        if (layerIndex >= 0)
        {
            var layer = file.AllLayers.FirstOrDefault(candidate => candidate.Index == layerIndex);
            if (layer is not null)
            {
                return FirstNonEmpty(layer.FullPath, layer.Name, $"Layer {layerIndex}");
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> ReadUserText(Rhino.DocObjects.ObjectAttributes attributes, GeometryBase geometry)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddUserStrings(values, attributes.GetUserStrings());
        AddUserStrings(values, geometry.GetUserStrings());
        return values;
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
