using System.Diagnostics;
using System.IO;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class DeveloperToolingService
{
    private static readonly ToolSpec[] Tools =
    [
        new("dotnet", "dotnet.exe"),
        new("Node.js", "node.exe"),
        new("Git", "git.exe"),
        new("VS Code", "code.exe"),
        new("Python", "python.exe")
    ];

    public AuditResult Analyze()
    {
        var items = Tools
            .Select(AnalyzeTool)
            .ToArray();

        return new AuditResult("OK", items);
    }

    private static AuditItem AnalyzeTool(ToolSpec spec)
    {
        var path = FindOnPath(spec.Executable);
        if (path is null)
        {
            return new AuditItem(
                "Developer tooling",
                spec.Name,
                "NOT_FOUND",
                null,
                "NOT_INSTALLED");
        }

        return new AuditItem(
            "Developer tooling",
            spec.Name,
            GetVersion(path) ?? "Installed",
            path,
            "INSTALLED");
    }
    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string? GetVersion(string executable)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ToolSpec(
        string Name,
        string Executable);
}
