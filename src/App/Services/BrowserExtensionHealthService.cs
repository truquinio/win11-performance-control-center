using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class BrowserExtensionHealthService(
    string? edgeUserDataRoot = null,
    IReadOnlySet<string>? policyRemovedExtensionIds = null)
{
    private static readonly Regex ChromiumExtensionIdPattern =
        new("^[a-p]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _edgeUserDataRoot =
        string.IsNullOrWhiteSpace(edgeUserDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Edge",
                "User Data")
            : Path.GetFullPath(edgeUserDataRoot);
    private readonly IReadOnlySet<string>? _policyRemovedExtensionIds =
        policyRemovedExtensionIds;

    public BrowserExtensionHealth AnalyzeEdge()
    {
        if (!Directory.Exists(_edgeUserDataRoot))
        {
            return new BrowserExtensionHealth(
                "Microsoft Edge",
                "NO_DATA",
                0,
                null,
                0,
                0,
                0,
                0,
                []);
        }

        var items = new List<BrowserExtensionHealthItem>();
        var profilesScanned = 0;
        bool? developerMode = null;
        var policyRemovedExtensionIds =
            _policyRemovedExtensionIds ?? ReadPolicyRemovedExtensionIds();

        foreach (var profile in EnumerateProfiles())
        {
            profilesScanned++;

            var profileDeveloperMode = ReadDeveloperMode(
                Path.Combine(profile, "Preferences"));
            if (profileDeveloperMode.HasValue)
            {
                developerMode = developerMode == true ||
                                profileDeveloperMode.Value;
            }

            AnalyzeProfile(profile, items, policyRemovedExtensionIds);
        }
        var installed = items.Count(item =>
            item.Status is "OK" or "INSTALLED_NO_LOCAL_DATA");
        var developerLoaded = items.Count(item => item.Status == "UNPACKED");
        var dataOnly = items.Count(item =>
            item.Status == "DATA_WITHOUT_INSTALLATION");
        var broken = items.Count(item =>
            item.Status is "CODE_MISSING" or "MANIFEST_MISSING");

        // Name collisions are advisory, not orphan candidates. Compare only
        // installed/unpacked entries within the SAME profile, never old data.
        var nameCollisions = items
            .Where(item => item.Status is "OK" or "INSTALLED_NO_LOCAL_DATA" or "UNPACKED")
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Profile, StringComparer.OrdinalIgnoreCase)
            .SelectMany(profile => profile
                .GroupBy(item => item.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new BrowserExtensionNameCollision(
                    profile.Key,
                    group.Key,
                    group.Select(item => item.ExtensionId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .Where(group => group.ExtensionIds.Count > 1))
            .OrderBy(group => group.Profile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var status = broken > 0 || dataOnly > 0 || nameCollisions.Length > 0
            ? "WARNING"
            : profilesScanned == 0
                ? "NO_DATA"
                : "OK";

        return new BrowserExtensionHealth(
            "Microsoft Edge",
            status,
            profilesScanned,
            developerMode,
            installed,
            developerLoaded,
            dataOnly,
            broken,
            [.. items
                .OrderByDescending(item => IsProblem(item.Status))
                .ThenBy(item => item.Profile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name ?? item.ExtensionId, StringComparer.OrdinalIgnoreCase)])
        {
            NameCollisions = nameCollisions
        };
    }

    private IEnumerable<string> EnumerateProfiles()
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_edgeUserDataRoot);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }
    private static void AnalyzeProfile(
        string profilePath,
        ICollection<BrowserExtensionHealthItem> destination,
        IReadOnlySet<string> policyRemovedExtensionIds)
    {
        var profileName = Path.GetFileName(profilePath);
        var extensionsRoot = Path.Combine(profilePath, "Extensions");
        var localSettingsRoot = Path.Combine(profilePath, "Local Extension Settings");
        var preferencesPath = Path.Combine(profilePath, "Preferences");
        var securePreferencesPath = Path.Combine(profilePath, "Secure Preferences");

        var physical = DiscoverPhysicalExtensions(extensionsRoot);
        var dataIds = EnumerateExtensionIds(localSettingsRoot).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var preferenceEntries = ReadPreferenceEntries(preferencesPath);
        foreach (var entry in ReadPreferenceEntries(securePreferencesPath))
        {
            preferenceEntries[entry.Key] = preferenceEntries.TryGetValue(
                entry.Key,
                out var existing)
                ? MergePreferenceInfo(existing, entry.Value)
                : entry.Value;
        }

        var allIds = new HashSet<string>(physical.Keys, StringComparer.OrdinalIgnoreCase);
        allIds.UnionWith(dataIds);
        allIds.UnionWith(preferenceEntries.Keys);

        foreach (var id in allIds)
        {
            physical.TryGetValue(id, out var physicalInfo);
            preferenceEntries.TryGetValue(id, out var preference);

            var externalPath = ResolvePreferencePath(profilePath, preference?.Path);
            var developerLoaded =
                preference?.Location == 4 &&
                !string.IsNullOrWhiteSpace(externalPath);

            var externalExists = developerLoaded &&
                                 Directory.Exists(externalPath);

            string status;
            string? installationPath;
            string? name;

            if (developerLoaded)
            {
                var externalManifest = externalPath is null
                    ? null
                    : Path.Combine(externalPath, "manifest.json");
                status = !externalExists
                    ? "CODE_MISSING"
                    : externalManifest is not null && File.Exists(externalManifest)
                        ? "UNPACKED"
                        : "MANIFEST_MISSING";
                installationPath = externalPath;
                name = ReadManifestName(externalManifest) ?? physicalInfo?.Name ?? VisiblePreferenceName(preference?.Name);
            }
            else if (physicalInfo is not null)
            {
                installationPath = physicalInfo.Path;
                name = physicalInfo.Name ?? VisiblePreferenceName(preference?.Name);
                status = physicalInfo.ManifestExists
                    ? dataIds.Contains(id)
                        ? "OK"
                        : "INSTALLED_NO_LOCAL_DATA"
                    : "MANIFEST_MISSING";
            }
            else if (policyRemovedExtensionIds.Contains(id))
            {
                installationPath = null;
                name = VisiblePreferenceName(preference?.Name);
                status = "POLICY_REMOVED";
            }
            else if (dataIds.Contains(id))
            {
                installationPath = null;
                name = VisiblePreferenceName(preference?.Name);
                status = "DATA_WITHOUT_INSTALLATION";
            }
            else
            {
                installationPath = externalPath;
                name = VisiblePreferenceName(preference?.Name);
                status = preference?.State == 1
                    ? "CODE_MISSING"
                    : "STALE_METADATA";
            }

            var dataBytes = dataIds.Contains(id)
                ? MeasureDirectory(Path.Combine(localSettingsRoot, id))
                : 0;

            destination.Add(new BrowserExtensionHealthItem(
                profileName,
                id,
                name,
                status,
                dataBytes,
                installationPath,
                developerLoaded));
        }
    }
    private static Dictionary<string, PhysicalExtensionInfo> DiscoverPhysicalExtensions(
        string root)
    {
        var result = new Dictionary<string, PhysicalExtensionInfo>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var id in EnumerateExtensionIds(root))
        {
            var idRoot = Path.Combine(root, id);
            string? selected = null;

            try
            {
                var candidates = Directory
                    .EnumerateDirectories(idRoot)
                    .OrderByDescending(
                        path => ParseExtensionVersion(Path.GetFileName(path)))
                    .ThenByDescending(
                        Path.GetFileName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                selected = candidates.FirstOrDefault(
                               path => File.Exists(
                                   Path.Combine(path, "manifest.json")))
                           ?? candidates.FirstOrDefault();
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only inspection degrades to a missing manifest state.
            }
            catch (IOException)
            {
                // The browser can rotate extension directories while running.
            }

            var manifest = selected is null
                ? null
                : Path.Combine(selected, "manifest.json");

            result[id] = new PhysicalExtensionInfo(
                selected ?? idRoot,
                manifest is not null && File.Exists(manifest),
                ReadManifestName(manifest));
        }

        return result;
    }

    private static IEnumerable<string> EnumerateExtensionIds(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (ChromiumExtensionIdPattern.IsMatch(name))
                yield return name;
        }
    }
    private static bool? ReadDeveloperMode(string preferencesPath)
    {
        using var document = TryReadJsonDocument(preferencesPath);
        if (document is null)
            return null;

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("extensions", out var extensions) ||
            extensions.ValueKind != JsonValueKind.Object ||
            !extensions.TryGetProperty("ui", out var ui) ||
            ui.ValueKind != JsonValueKind.Object ||
            !ui.TryGetProperty("developer_mode", out var developerMode))
        {
            return null;
        }

        return developerMode.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static Dictionary<string, PreferenceExtensionInfo> ReadPreferenceEntries(
        string preferencesPath)
    {
        var result = new Dictionary<string, PreferenceExtensionInfo>(
            StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(preferencesPath))
            return result;

        using var document = TryReadJsonDocument(preferencesPath);
        if (document is null)
            return result;

        // Edge rewrites these files while running and they can be truncated
        // or replaced by another JSON shape; anything unexpected is no data.
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("extensions", out var extensions) ||
            extensions.ValueKind != JsonValueKind.Object ||
            !extensions.TryGetProperty("settings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var extension in settings.EnumerateObject())
        {
            if (!ChromiumExtensionIdPattern.IsMatch(extension.Name) ||
                extension.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = extension.Value;
            var path = TryGetString(value, "path");
            var location = TryGetInt32(value, "location");
            var state = TryGetInt32(value, "state");
            string? name = null;

            if (value.TryGetProperty("manifest", out var manifest) &&
                manifest.ValueKind == JsonValueKind.Object)
            {
                name = TryGetString(manifest, "name");
            }

            result[extension.Name] = new PreferenceExtensionInfo(
                path,
                location,
                state,
                name);
        }

        return result;
    }
    private static PreferenceExtensionInfo MergePreferenceInfo(
        PreferenceExtensionInfo primary,
        PreferenceExtensionInfo overlay) =>
        new(
            overlay.Path ?? primary.Path,
            overlay.Location ?? primary.Location,
            overlay.State ?? primary.State,
            overlay.Name ?? primary.Name);

    private static string? ResolvePreferencePath(
        string profilePath,
        string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        try
        {
            var direct = Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(profilePath, configuredPath));
            if (Path.IsPathRooted(configuredPath) || Directory.Exists(direct))
                return direct;

            // Edge's "Secure Preferences" sometimes stores a relative path
            // like <extension-id>\\<version> under Default/Extensions, not
            // directly under Default. The old resolver falsely reported
            // installed Edge add-ons as CODE_MISSING.
            if (Regex.IsMatch(configuredPath,
                @"^[a-p]{32}[\\/][0-9][a-zA-Z0-9._-]*$",
                RegexOptions.CultureInvariant))
            {
                var packed = Path.GetFullPath(
                    Path.Combine(profilePath, "Extensions", configuredPath));
                if (Directory.Exists(packed))
                    return packed;
            }

            return direct;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private static string? VisiblePreferenceName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase)
            ? value.Trim()
            : null;

    private static string? ReadManifestName(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) ||
            !File.Exists(manifestPath))
        {
            return null;
        }

        using var document = TryReadJsonDocument(manifestPath);
        if (document is null)
            return null;

        var manifest = document.RootElement;
        var name = TryGetString(manifest, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (!name.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase))
            return name.Trim();
        if (!name.EndsWith("__", StringComparison.Ordinal) || name.Length <= 8)
            return null;

        // Chromium manifests can contain __MSG_name__ rather than a display
        // name. Never group unresolved tokens as if they were identical apps.
        var key = name[6..^2];
        if (!Regex.IsMatch(key, "^[a-zA-Z0-9_]{1,80}$", RegexOptions.CultureInvariant))
            return null;
        var defaultLocale = TryGetString(manifest, "default_locale");
        var localeNames = new[] { defaultLocale, "en", "en_US", "es", "es_ES" }
            .Where(locale => !string.IsNullOrWhiteSpace(locale))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var locale in localeNames)
        {
            if (!Regex.IsMatch(locale!, "^[a-zA-Z_]{2,12}$", RegexOptions.CultureInvariant))
                continue;
            var messagePath = Path.Combine(
                Path.GetDirectoryName(manifestPath)!, "_locales", locale!, "messages.json");
            using var messages = TryReadJsonDocument(messagePath);
            if (messages is null || messages.RootElement.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var property in messages.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                var resolved = TryGetString(property.Value, "message");
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved.Trim();
            }
        }

        return null;
    }
    private static long MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
            return 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total = checked(total + new FileInfo(file).Length);
                    }
                    catch (Exception ex) when (
                        ex is IOException or
                        UnauthorizedAccessException or
                        OverflowException)
                    {
                        // A single inaccessible file must not abort the audit.
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.ReparsePoint) == 0)
                            pending.Push(directory);
                    }
                    catch (Exception ex) when (
                        ex is IOException or UnauthorizedAccessException)
                    {
                        // Ignore inaccessible descendants.
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // Profile content can change while Edge is running.
            }
        }

        return total;
    }

    private static IReadOnlySet<string> ReadPolicyRemovedExtensionIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge");
                if (key?.GetValue("ExtensionSettings") is not string json ||
                    string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var extension in document.RootElement.EnumerateObject())
                {
                    if (!ChromiumExtensionIdPattern.IsMatch(extension.Name) ||
                        extension.Value.ValueKind != JsonValueKind.Object ||
                        !extension.Value.TryGetProperty("installation_mode", out var mode) ||
                        mode.ValueKind != JsonValueKind.String ||
                        !string.Equals(
                            mode.GetString(),
                            "removed",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(extension.Name);
                }
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                JsonException)
            {
                // Policy inspection is read-only and must degrade safely.
            }
        }

        return result;
    }

    private static JsonDocument? TryReadJsonDocument(string path)
    {
        if (!File.Exists(path))
            return null;

        const int attempts = 3;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                return JsonDocument.Parse(stream);
            }
            catch (JsonException) when (attempt < attempts - 1)
            {
                Thread.Sleep(20);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    private static Version ParseExtensionVersion(string? directoryName)
    {
        var versionText = directoryName?.Split('_', 2)[0];
        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(0, 0, 0, 0);
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryGetInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool IsProblem(string status) =>
        status is "DATA_WITHOUT_INSTALLATION" or "CODE_MISSING" or "MANIFEST_MISSING";

    private sealed record PhysicalExtensionInfo(
        string Path,
        bool ManifestExists,
        string? Name);

    private sealed record PreferenceExtensionInfo(
        string? Path,
        int? Location,
        int? State,
        string? Name);
}
