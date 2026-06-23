using Rhino3dmToIfc.Console.Models;
using Rhino3dmToIfc.Console.Services;

var parser = new CommandLineOptionsParser();
var options = parser.Parse(args);

if (options is null)
{
    CommandLineOptionsParser.PrintUsage();
    return 1;
}

if (!File.Exists(options.InputPath))
{
    System.Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
    return 1;
}

if (!string.Equals(Path.GetExtension(options.InputPath), ".3dm", StringComparison.OrdinalIgnoreCase))
{
    System.Console.Error.WriteLine("Input file must have a .3dm extension.");
    return 1;
}

if (!string.Equals(Path.GetExtension(options.OutputPath), ".ifc", StringComparison.OrdinalIgnoreCase))
{
    System.Console.Error.WriteLine("Output file must have a .ifc extension.");
    return 1;
}

var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
if (!string.IsNullOrWhiteSpace(outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
}

var logPath = Path.Combine(outputDirectory ?? Directory.GetCurrentDirectory(), "model_export.log");
options.LogPath = logPath;

var summary = new ExportSummary
{
    OutputPath = options.OutputPath,
    LogPath = logPath
};

await using var log = new LogService(logPath);
log.Info("Rhino3dmToIfc.Console export started.");
log.Info($"Input path: {options.InputPath}");
log.Info($"Output path: {options.OutputPath}");

try
{
    var reader = new ThreeDmReader(log);
    var classifier = new ObjectClassificationService(log);
    var modelBuilder = new IfcModelBuilder(log, new IfcGeometryBuilder(log), new IfcPropertySetBuilder(log));

    var objects = reader.Read(options.InputPath);
    summary.TotalObjects = objects.Count;
    log.Info($"Total readable objects: {summary.TotalObjects}");

    var classifiedObjects = classifier.Classify(objects, options, summary);
    modelBuilder.BuildAndSave(classifiedObjects, options, summary);

    log.Info("Export completed.");
    log.WriteSummary(summary);
    PrintSummary(summary);
    return summary.FailedObjects > 0 ? 2 : 0;
}
catch (Exception ex)
{
    summary.FailedObjects++;
    log.Error("Fatal export failure.", ex);
    log.WriteSummary(summary);
    System.Console.Error.WriteLine($"Export failed: {ex.Message}");
    System.Console.Error.WriteLine($"Log: {logPath}");
    return 1;
}

static void PrintSummary(ExportSummary summary)
{
    System.Console.WriteLine();
    System.Console.WriteLine("Rhino3dm to IFC4 export summary");
    System.Console.WriteLine("--------------------------------");
    System.Console.WriteLine($"Total objects:       {summary.TotalObjects}");
    System.Console.WriteLine($"Exported objects:    {summary.ExportedObjects}");
    System.Console.WriteLine($"Skipped objects:     {summary.SkippedObjects}");
    System.Console.WriteLine($"Mesh objects:        {summary.MeshObjects}");
    System.Console.WriteLine($"Unsupported Breps:   {summary.UnsupportedBrepObjects}");
    System.Console.WriteLine($"Unsupported surfaces:{summary.UnsupportedSurfaceObjects}");
    System.Console.WriteLine($"Unsupported extrusions: {summary.UnsupportedExtrusionObjects}");
    System.Console.WriteLine($"Unsupported other:   {summary.UnsupportedOtherObjects}");
    System.Console.WriteLine($"Failed objects:      {summary.FailedObjects}");
    System.Console.WriteLine($"IFC output:          {summary.OutputPath}");
    System.Console.WriteLine($"Log file:            {summary.LogPath}");
}

