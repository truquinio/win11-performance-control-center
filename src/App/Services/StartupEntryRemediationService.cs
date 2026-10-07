using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StartupEntryRemediationService
{
    private static readonly string[] ProtectedKeywords =
    [
        "security", "defender", "antivirus", "malwarebytes",
        "openai", "chatgpt", "claude", "desktop commander",
        "desktopcommander", "mcp", "ollama", "pm2", "node",
        "python", "nssm", "onedrive", "windows security"
    ];

    private static readonly RegistryLocation[] Locations =
    [
        new("HKCU", RegistryHive.CurrentUser, RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new("HKCU", RegistryHive.CurrentUser, RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new("HKLM", RegistryHive.LocalMachine, RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32,
            @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new("HKLM", RegistryHive.LocalMachine, RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32,
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce")
    ];

    private readonly string statePath;
    private readonly bool evaluationMode;

    public StartupEntryRemediationService(
        string? statePath = null,
        bool evaluationMode = false)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.StartupEntryState
            : Path.GetFullPath(statePath);
        this.evaluationMode = evaluationMode;

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task<StartupEntryPreview> PreviewAsync() =>
        Task.Run(() =>
        {
            var snapshots = LoadState();
            var active = ReadEntries()
                .Where(item => !snapshots.ContainsKey(item.EntryId))
                .ToList();

            var items = new List<StartupEntryCandidate>();
            foreach (var entry in active)
                items.Add(ToCandidate(entry, restoreAvailable: false));

            foreach (var snapshot in snapshots.Values)
            {
                items.Add(new StartupEntryCandidate(
                    snapshot.EntryId,
                    snapshot.Name,
                    snapshot.Command,
                    snapshot.Location,
                    snapshot.Scope,
                    snapshot.View,
                    false,
                    false,
                    "Deshabilitado por Win11 Performance Control Center.",
                    true));
            }

            var ordered = items
                .OrderBy(item => item.Enabled ? 0 : 1)
                .ThenBy(item => item.Protected)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToArray();

            return new StartupEntryPreview(
                ordered.Count(item => item.Enabled),
                ordered.Count(item => item.Enabled && !item.Protected),
                ordered.Count(item => item.Enabled && item.Protected),
                ordered.Count(item => !item.Enabled && item.RestoreAvailable),
                ordered);
        });

    public Task<StartupEntryChangeResult> DisableAsync(
        string entryId) =>
        Task.Run(() =>
        {
            ValidateEntryId(entryId);
            var entry = ReadEntries().FirstOrDefault(item =>
                string.Equals(
                    item.EntryId,
                    entryId,
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException(
                    "La entrada de autoarranque ya no existe.");

            var candidate = ToCandidate(entry, restoreAvailable: false);
            if (candidate.Protected)
                throw new InvalidOperationException(
                    "Entrada protegida: " + candidate.Reason);

            var state = LoadState();
            if (!state.ContainsKey(entry.EntryId))
            {
                state[entry.EntryId] = new StartupEntrySnapshot(
                    entry.EntryId,
                    entry.Name,
                    entry.Command,
                    entry.Location.Label,
                    entry.Location.Scope,
                    entry.Location.View.ToString(),
                    (int)entry.ValueKind,
                    DateTimeOffset.UtcNow);
                PersistState(state);
            }

            if (evaluationMode)
            {
                return new StartupEntryChangeResult(
                    true,
                    "DISABLED",
                    entry.EntryId,
                    entry.Name,
                    false,
                    true,
                    "EVALUATION");
            }

            using var baseKey = RegistryKey.OpenBaseKey(
                entry.Location.Hive,
                entry.Location.View);
            using var key = baseKey.OpenSubKey(
                entry.Location.SubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "La clave de autoarranque ya no existe.");

            var current = Convert.ToString(
                key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            if (!string.Equals(
                    current,
                    entry.Command,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "La entrada cambió desde el análisis; vuelve a previsualizar.");
            }

            key.DeleteValue(entry.Name, throwOnMissingValue: true);
            var verified = key.GetValue(entry.Name, null) is null;

            return new StartupEntryChangeResult(
                verified,
                verified ? "DISABLED" : "VERIFY_FAILED",
                entry.EntryId,
                entry.Name,
                false,
                true,
                verified
                    ? "Entrada quitada del autoarranque sin cerrar el proceso actual."
                    : "Windows sigue mostrando la entrada después del cambio.");
        });

    public Task<StartupEntryChangeResult> RestoreAsync(
        string entryId) =>
        Task.Run(() =>
        {
            ValidateEntryId(entryId);
            var state = LoadState();
            if (!state.TryGetValue(entryId, out var snapshot))
            {
                return new StartupEntryChangeResult(
                    true,
                    "NO_SNAPSHOT",
                    entryId,
                    entryId,
                    false,
                    false,
                    "No existe estado previo guardado.");
            }

            var location = ResolveLocation(snapshot) ??
                throw new InvalidOperationException(
                    "El snapshot no pertenece a una ubicación allowlisted.");

            if (evaluationMode)
            {
                state.Remove(entryId);
                PersistState(state);
                return new StartupEntryChangeResult(
                    true,
                    "RESTORED",
                    entryId,
                    snapshot.Name,
                    true,
                    false,
                    "EVALUATION");
            }

            using var baseKey = RegistryKey.OpenBaseKey(
                location.Hive,
                location.View);
            using var key = baseKey.CreateSubKey(
                location.SubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir la clave de autoarranque.");

            if (key.GetValue(snapshot.Name, null) is not null)
            {
                throw new InvalidOperationException(
                    "Ya existe una entrada con ese nombre; no se sobrescribe.");
            }

            var kind = Enum.IsDefined(
                typeof(RegistryValueKind),
                snapshot.ValueKind)
                ? (RegistryValueKind)snapshot.ValueKind
                : RegistryValueKind.String;

            if (kind is not (
                RegistryValueKind.String or
                RegistryValueKind.ExpandString))
            {
                throw new InvalidOperationException(
                    "Tipo de registro no permitido para rollback.");
            }

            key.SetValue(
                snapshot.Name,
                snapshot.Command,
                kind);

            var restored = Convert.ToString(
                key.GetValue(
                    snapshot.Name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames));
            var success = string.Equals(
                restored,
                snapshot.Command,
                StringComparison.Ordinal);

            if (success)
            {
                state.Remove(entryId);
                PersistState(state);
            }

            return new StartupEntryChangeResult(
                success,
                success ? "RESTORED" : "VERIFY_FAILED",
                entryId,
                snapshot.Name,
                success,
                !success,
                success
                    ? "Entrada restaurada y verificada."
                    : "La restauración no pudo verificarse.");
        });

    private StartupEntryCandidate ToCandidate(
        StartupEntryDescriptor entry,
        bool restoreAvailable)
    {
        var combined = string.Join(
            " ",
            entry.Name,
            entry.Command);

        if (ProtectedKeywords.Any(keyword =>
                combined.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new StartupEntryCandidate(
                entry.EntryId,
                entry.Name,
                entry.Command,
                entry.Location.Label,
                entry.Location.Scope,
                entry.Location.View.ToString(),
                true,
                true,
                "Protegido por política local: seguridad, nube, IA o automatización.",
                restoreAvailable);
        }

        var executable = ResolveExecutable(entry.Command);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new StartupEntryCandidate(
                entry.EntryId,
                entry.Name,
                entry.Command,
                entry.Location.Label,
                entry.Location.Scope,
                entry.Location.View.ToString(),
                true,
                true,
                "No se pudo resolver el ejecutable de forma segura.",
                restoreAvailable);
        }

        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (IsUnderRoot(executable, windows))
        {
            return new StartupEntryCandidate(
                entry.EntryId,
                entry.Name,
                entry.Command,
                entry.Location.Label,
                entry.Location.Scope,
                entry.Location.View.ToString(),
                true,
                true,
                "Entrada asociada a un ejecutable dentro de Windows.",
                restoreAvailable);
        }

        if (!evaluationMode && !File.Exists(executable))
        {
            return new StartupEntryCandidate(
                entry.EntryId,
                entry.Name,
                entry.Command,
                entry.Location.Label,
                entry.Location.Scope,
                entry.Location.View.ToString(),
                true,
                true,
                "El ejecutable ya no existe; se requiere revisión manual antes de modificar el registro.",
                restoreAvailable);
        }

        return new StartupEntryCandidate(
            entry.EntryId,
            entry.Name,
            entry.Command,
            entry.Location.Label,
            entry.Location.Scope,
            entry.Location.View.ToString(),
            true,
            false,
            "Entrada de terceros revisable. Deshabilitarla no cierra el proceso actual.",
            restoreAvailable);
    }

    private IReadOnlyList<StartupEntryDescriptor> ReadEntries()
    {
        if (evaluationMode)
            return SyntheticEntries();

        var result = new List<StartupEntryDescriptor>();
        foreach (var location in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(
                    location.Hive,
                    location.View);
                using var key = baseKey.OpenSubKey(
                    location.SubKey,
                    writable: false);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                {
                    var raw = key.GetValue(
                        name,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var command = Convert.ToString(raw);
                    if (string.IsNullOrWhiteSpace(command))
                        continue;

                    RegistryValueKind kind;
                    try
                    {
                        kind = key.GetValueKind(name);
                    }
                    catch
                    {
                        continue;
                    }

                    if (kind is not (
                        RegistryValueKind.String or
                        RegistryValueKind.ExpandString))
                    {
                        continue;
                    }

                    result.Add(new StartupEntryDescriptor(
                        MakeEntryId(location, name),
                        name,
                        command,
                        kind,
                        location));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (IOException)
            {
            }
        }

        return result
            .DistinctBy(item => item.EntryId)
            .ToArray();
    }

    private static IReadOnlyList<StartupEntryDescriptor> SyntheticEntries()
    {
        var userRun = Locations[0];
        return
        [
            new StartupEntryDescriptor(
                MakeEntryId(userRun, "DemoVendor"),
                "DemoVendor",
                @"C:\Program Files\DemoVendor\agent.exe --background",
                RegistryValueKind.String,
                userRun),
            new StartupEntryDescriptor(
                MakeEntryId(userRun, "PM2"),
                "PM2",
                @"C:\Program Files\nodejs\node.exe pm2 resurrect",
                RegistryValueKind.String,
                userRun)
        ];
    }

    private Dictionary<string, StartupEntrySnapshot> LoadState()
    {
        try
        {
            if (!File.Exists(statePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var value = JsonSerializer.Deserialize<
                Dictionary<string, StartupEntrySnapshot>>(
                    File.ReadAllText(statePath),
                    HostBridge.JsonOptions);
            return value is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistState(
        Dictionary<string, StartupEntrySnapshot> state)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(state, HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

    private static RegistryLocation? ResolveLocation(
        StartupEntrySnapshot snapshot) =>
        Locations.FirstOrDefault(location =>
            string.Equals(
                location.Scope,
                snapshot.Scope,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                location.View.ToString(),
                snapshot.View,
                StringComparison.Ordinal) &&
            string.Equals(
                location.Label,
                snapshot.Location,
                StringComparison.OrdinalIgnoreCase));

    private static string MakeEntryId(
        RegistryLocation location,
        string name)
    {
        var raw = string.Join(
            "|",
            location.Scope,
            location.View,
            location.SubKey,
            name);
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24];
    }

    private static void ValidateEntryId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) ||
            entryId.Length != 24 ||
            !entryId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Entry ID de autoarranque no permitido.");
        }
    }

    private static string? ResolveExecutable(string command)
    {
        var value = Environment
            .ExpandEnvironmentVariables(command.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1
                ? SafeFullPath(value[1..end])
                : null;
        }

        var exe = value.IndexOf(
            ".exe",
            StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return SafeFullPath(value[..(exe + 4)]);

        return null;
    }

    private static string? SafeFullPath(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderRoot(
        string path,
        string root)
    {
        try
        {
            var p = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar);
            return p.Equals(
                    r,
                    StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(
                    r + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private sealed record RegistryLocation(
        string Scope,
        RegistryHive Hive,
        RegistryView View,
        string SubKey)
    {
        public string Label =>
            Scope + @"\" + SubKey;
    }

    private sealed record StartupEntryDescriptor(
        string EntryId,
        string Name,
        string Command,
        RegistryValueKind ValueKind,
        RegistryLocation Location);
}

public sealed record StartupEntryCandidate(
    string EntryId,
    string Name,
    string Command,
    string Location,
    string Scope,
    string View,
    bool Enabled,
    bool Protected,
    string Reason,
    bool RestoreAvailable);

public sealed record StartupEntryPreview(
    int RegisteredCount,
    int EligibleCount,
    int ProtectedCount,
    int DisabledByAppCount,
    IReadOnlyList<StartupEntryCandidate> Entries);

public sealed record StartupEntrySnapshot(
    string EntryId,
    string Name,
    string Command,
    string Location,
    string Scope,
    string View,
    int ValueKind,
    DateTimeOffset CapturedAt);

public sealed record StartupEntryChangeResult(
    bool Success,
    string Status,
    string EntryId,
    string Name,
    bool Enabled,
    bool RestoreAvailable,
    string Detail);
