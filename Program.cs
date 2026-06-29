using Rhino3dmToIfc.Console;
using Rhino3dmToIfc.Console.Models;
using Rhino3dmToIfc.Console.Services;

var options = new CommandLineOptionsParser().Parse(args);
if (options is null)
{
    CommandLineOptionsParser.PrintUsage();
    return 1;
}

try
{
    System.Console.WriteLine("Starting export...");
    var summary = await new Rhino3dmToIfcConverter().ConvertAsync(options);
    PrintSummary(summary);
    return summary.FailedObjects > 0 ? 2 : 0;
}
catch (Exception ex)
{
    System.Console.Error.WriteLine($"Export failed: {ex.Message}");
    if (!string.IsNullOrWhiteSpace(options.LogPath))
    {
        System.Console.Error.WriteLine($"Log: {options.LogPath}");
    }

    return 1;
}

static void PrintSummary(ExportSummary summary)
{
    System.Console.WriteLine();
    System.Console.WriteLine("Rhino3dm to IFC4 export summary");
    System.Console.WriteLine("--------------------------------");
    System.Console.WriteLine($"Total objects:          {summary.TotalObjects}");
    System.Console.WriteLine($"Exported objects:       {summary.ExportedObjects}");
    System.Console.WriteLine($"Skipped objects:        {summary.SkippedObjects}");
    System.Console.WriteLine($"Mesh objects:           {summary.MeshObjects}");
    System.Console.WriteLine($"Unsupported Breps:      {summary.UnsupportedBrepObjects}");
    System.Console.WriteLine($"Unsupported surfaces:   {summary.UnsupportedSurfaceObjects}");
    System.Console.WriteLine($"Unsupported extrusions: {summary.UnsupportedExtrusionObjects}");
    System.Console.WriteLine($"Unsupported other:      {summary.UnsupportedOtherObjects}");
    System.Console.WriteLine($"Failed objects:         {summary.FailedObjects}");
    System.Console.WriteLine($"IFC output:             {summary.OutputPath}");
    System.Console.WriteLine($"Log file:               {summary.LogPath}");
}
