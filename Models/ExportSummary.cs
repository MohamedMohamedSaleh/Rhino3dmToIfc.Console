namespace Rhino3dmToIfc.Console.Models;

public sealed class ExportSummary
{
    public int TotalObjects { get; set; }

    public int ExportedObjects { get; set; }

    public int SkippedObjects { get; set; }

    public int MeshObjects { get; set; }

    public int UnsupportedBrepObjects { get; set; }

    public int UnsupportedSurfaceObjects { get; set; }

    public int UnsupportedExtrusionObjects { get; set; }

    public int UnsupportedOtherObjects { get; set; }

    public int FailedObjects { get; set; }

    public string OutputPath { get; set; } = string.Empty;

    public string LogPath { get; set; } = string.Empty;

    public int WarningCount { get; set; }
}
