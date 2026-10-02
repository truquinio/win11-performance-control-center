using System.Text.Json;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class BrowserExtensionHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wpcc-edge-health-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnalyzeEdge_DetectsInstalledDataOnlyAndUnpackedExtensions()
    {
        var profile = Path.Combine(_root, "Default");
        var installedId = new string('a', 32);
        var dataOnlyId = new string('b', 32);
        var unpackedId = new string('c', 32);
        var unpackedPath = Path.Combine(_root, "Unpacked", unpackedId);

        Directory.CreateDirectory(
            Path.Combine(profile, "Extensions", installedId, "1.0.0"));
        File.WriteAllText(
            Path.Combine(profile, "Extensions", installedId, "1.0.0", "manifest.json"),
            """{"name":"Installed fixture","version":"1.0.0","manifest_version":3}""");

        Directory.CreateDirectory(
            Path.Combine(profile, "Local Extension Settings", installedId));
        File.WriteAllText(
            Path.Combine(profile, "Local Extension Settings", installedId, "data.bin"),
            "installed-data");

        Directory.CreateDirectory(
            Path.Combine(profile, "Local Extension Settings", dataOnlyId));
        File.WriteAllText(
            Path.Combine(profile, "Local Extension Settings", dataOnlyId, "data.bin"),
            "orphan-data");

        Directory.CreateDirectory(unpackedPath);
        File.WriteAllText(
            Path.Combine(unpackedPath, "manifest.json"),
            """{"name":"Unpacked fixture","version":"1.0.0","manifest_version":3}""");

        var preferences = new
        {
            extensions = new
            {
                ui = new { developer_mode = true },
                settings = new Dictionary<string, object>
                {
                    [installedId] = new
                    {
                        location = 5,
                        path = installedId,
                        manifest = new { name = "Installed fixture" }
                    },
                    [unpackedId] = new
                    {
                        location = 4,
                        path = unpackedPath,
                        manifest = new { name = "Unpacked fixture" }
                    }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(preferences));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.Equal("WARNING", result.Status);
        Assert.Equal(1, result.ProfilesScanned);
        Assert.True(result.DeveloperMode);
        Assert.Equal(1, result.InstalledCount);
        Assert.Equal(1, result.DeveloperLoadedCount);
        Assert.Equal(1, result.DataOnlyCount);
        Assert.Equal(0, result.BrokenCount);

        Assert.Contains(
            result.Items,
            item => item.ExtensionId == installedId &&
                    item.Status == "OK" &&
                    item.DataBytes > 0);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == dataOnlyId &&
                    item.Status == "DATA_WITHOUT_INSTALLATION" &&
                    item.DataBytes > 0);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == unpackedId &&
                    item.Status == "UNPACKED" &&
                    item.DeveloperLoaded);
    }

    [Fact]
    public void AnalyzeEdge_DetectsMissingUnpackedCode()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('d', 32);
        var missingPath = Path.Combine(_root, "Missing", id);
        var preferences = new
        {
            extensions = new
            {
                settings = new Dictionary<string, object>
                {
                    [id] = new
                    {
                        location = 4,
                        path = missingPath,
                        manifest = new { name = "Missing fixture" }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(preferences));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.Equal("WARNING", result.Status);
        Assert.Equal(1, result.BrokenCount);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id && item.Status == "CODE_MISSING");
    }

    [Fact]
    public void AnalyzeEdge_MergesSecurePreferencesAndReadsDeveloperMode()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('e', 32);
        var unpackedPath = Path.Combine(_root, "SecureOnly", id);
        Directory.CreateDirectory(unpackedPath);
        File.WriteAllText(
            Path.Combine(unpackedPath, "manifest.json"),
            """{"name":"Secure fixture","version":"1.0.0","manifest_version":3}""");

        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    ui = new { developer_mode = false }
                }
            }));

        File.WriteAllText(
            Path.Combine(profile, "Secure Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [id] = new
                        {
                            location = 4,
                            path = unpackedPath,
                            manifest = new { name = "Secure fixture" }
                        }
                    }
                }
            }));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.False(result.DeveloperMode);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id &&
                    item.Status == "UNPACKED" &&
                    item.DeveloperLoaded);
    }

    [Fact]
    public void AnalyzeEdge_UsesNewestNumericExtensionVersion()
    {
        var profile = Path.Combine(_root, "Default");
        var id = new string('f', 32);
        var extensionRoot = Path.Combine(profile, "Extensions", id);

        Directory.CreateDirectory(Path.Combine(extensionRoot, "9.9.9_0"));
        File.WriteAllText(
            Path.Combine(extensionRoot, "9.9.9_0", "manifest.json"),
            """{"name":"Old fixture","version":"9.9.9","manifest_version":3}""");

        Directory.CreateDirectory(Path.Combine(extensionRoot, "10.0.0_0"));
        File.WriteAllText(
            Path.Combine(extensionRoot, "10.0.0_0", "manifest.json"),
            """{"name":"New fixture","version":"10.0.0","manifest_version":3}""");

        Directory.CreateDirectory(Path.Combine(profile, "Local Extension Settings", id));
        File.WriteAllText(
            Path.Combine(profile, "Local Extension Settings", id, "data.bin"),
            "data");

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        var item = Assert.Single(result.Items, item => item.ExtensionId == id);
        Assert.Equal("New fixture", item.Name);
        Assert.Contains("10.0.0_0", item.InstallationPath, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeEdge_UnpackedFolderWithoutManifestIsBroken()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('g', 32);
        var unpackedPath = Path.Combine(_root, "UnpackedMissingManifest", id);
        Directory.CreateDirectory(unpackedPath);

        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [id] = new
                        {
                            location = 4,
                            path = unpackedPath,
                            manifest = new { name = "Broken unpacked fixture" }
                        }
                    }
                }
            }));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.Equal("WARNING", result.Status);
        Assert.Equal(1, result.BrokenCount);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id && item.Status == "MANIFEST_MISSING");
    }

    [Fact]
    public void AnalyzeEdge_PolicyRemovedMetadataIsNotReportedBroken()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('h', 32);
        File.WriteAllText(
            Path.Combine(profile, "Secure Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [id] = new
                        {
                            location = 1,
                            path = id,
                            manifest = new { name = "Removed by policy" }
                        }
                    }
                }
            }));

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var result = new BrowserExtensionHealthService(_root, removed).AnalyzeEdge();

        Assert.Equal("OK", result.Status);
        Assert.Equal(0, result.BrokenCount);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id && item.Status == "POLICY_REMOVED");
    }

    [Fact]
    public void AnalyzeEdge_DisabledMetadataWithoutCodeIsNotReportedBroken()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('i', 32);
        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [id] = new
                        {
                            location = 1,
                            state = 0,
                            path = id,
                            manifest = new { name = "Disabled stale fixture" }
                        }
                    }
                }
            }));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.Equal("OK", result.Status);
        Assert.Equal(0, result.BrokenCount);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id &&
                    item.Status == "STALE_METADATA");
    }

    [Fact]
    public void AnalyzeEdge_EnabledMetadataWithoutCodeIsReportedBroken()
    {
        var profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(profile);

        var id = new string('j', 32);
        File.WriteAllText(
            Path.Combine(profile, "Preferences"),
            JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [id] = new
                        {
                            location = 1,
                            state = 1,
                            path = id,
                            manifest = new { name = "Enabled missing fixture" }
                        }
                    }
                }
            }));

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();

        Assert.Equal("WARNING", result.Status);
        Assert.Equal(1, result.BrokenCount);
        Assert.Contains(
            result.Items,
            item => item.ExtensionId == id &&
                    item.Status == "CODE_MISSING");
    }

    [Fact]
    public void AnalyzeEdge_PrefersNewestValidManifestDuringExtensionRotation()
    {
        var profile = Path.Combine(_root, "Default");
        var id = new string('e', 32);
        var version1 = Path.Combine(profile, "Extensions", id, "1.0.0");
        var version2 = Path.Combine(profile, "Extensions", id, "2.0.0");
        Directory.CreateDirectory(version1);
        Directory.CreateDirectory(version2);
        File.WriteAllText(
            Path.Combine(version1, "manifest.json"),
            """{"name":"Stable fixture","version":"1.0.0","manifest_version":3}""");

        Directory.CreateDirectory(
            Path.Combine(profile, "Local Extension Settings", id));
        File.WriteAllText(
            Path.Combine(profile, "Local Extension Settings", id, "data.bin"),
            "fixture");

        var result = new BrowserExtensionHealthService(_root).AnalyzeEdge();
        var item = Assert.Single(
            result.Items,
            candidate => candidate.ExtensionId == id);

        Assert.Equal("OK", item.Status);
        Assert.NotNull(item.InstallationPath);
        Assert.Contains(
            Path.Combine(id, "1.0.0"),
            item.InstallationPath!,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup must not hide test assertions.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup must not hide test assertions.
        }
    }
}
