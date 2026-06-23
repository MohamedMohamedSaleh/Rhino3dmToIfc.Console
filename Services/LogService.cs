using Rhino3dmToIfc.Console.Models;

namespace Rhino3dmToIfc.Console.Services;

public sealed class LogService : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    public LogService(string logPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(logPath, append: false)
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message} {exception}");
    }

    public void WriteSummary(ExportSummary summary)
    {
        Info("Final summary:");
        Info($"  Total objects: {summary.TotalObjects}");
        Info($"  Exported objects: {summary.ExportedObjects}");
        Info($"  Skipped objects: {summary.SkippedObjects}");
        Info($"  Mesh objects: {summary.MeshObjects}");
        Info($"  Unsupported Brep objects: {summary.UnsupportedBrepObjects}");
        Info($"  Unsupported Surface objects: {summary.UnsupportedSurfaceObjects}");
        Info($"  Unsupported Extrusion objects: {summary.UnsupportedExtrusionObjects}");
        Info($"  Unsupported Other objects: {summary.UnsupportedOtherObjects}");
        Info($"  Failed objects: {summary.FailedObjects}");
        Info($"  Warnings: {summary.WarningCount}");
        Info($"  Output path: {summary.OutputPath}");
        Info($"  Log path: {summary.LogPath}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
        _writer.WriteLine(line);

        if (level is "ERROR")
        {
            System.Console.Error.WriteLine(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }
}
