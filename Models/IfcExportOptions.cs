namespace Rhino3dmToIfc.Console.Models;

public sealed class IfcExportOptions
{
    public string InputPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public string DefaultStorey { get; init; } = "Level 1";

    public string DefaultIfcType { get; init; } = "IfcBuildingElementProxy";

    public string LogPath { get; set; } = string.Empty;
}
