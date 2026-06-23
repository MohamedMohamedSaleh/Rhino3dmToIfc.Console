using Rhino3dmToIfc.Console.Models;

namespace Rhino3dmToIfc.Console.Services;

public sealed class CommandLineOptionsParser
{
    private static readonly HashSet<string> RequiredOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--input",
        "--output"
    };

    public IfcExportOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            values[key] = args[++i];
        }

        if (RequiredOptions.Any(required => !values.ContainsKey(required)))
        {
            return null;
        }

        return new IfcExportOptions
        {
            InputPath = values["--input"],
            OutputPath = values["--output"],
            DefaultStorey = values.GetValueOrDefault("--default-storey", "Level 1"),
            DefaultIfcType = values.GetValueOrDefault("--default-ifc-type", "IfcBuildingElementProxy")
        };
    }

    public static void PrintUsage()
    {
        System.Console.WriteLine("Usage:");
        System.Console.WriteLine("  dotnet run -- --input \"/path/model.3dm\" --output \"/path/model.ifc\" --default-storey \"Level 1\" --default-ifc-type \"IfcBuildingElementProxy\"");
    }
}
