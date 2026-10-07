using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class WindowsEvidenceService
{
    private const int MaxIfeoEntries = 160;
    private const int MaxComEntriesPerView = 300;
    private const int MaxCertificateItems = 120;

    public WindowsEvidenceReport AnalyzeRegistry()
    {
        var items = new List<WindowsEvidenceItem>();

        ReadRegistryValue(
            items,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
            "AppInit_DLLs",
            "AppInit DLLs");
        ReadRegistryValue(
            items,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
            "LoadAppInit_DLLs",
            "Load AppInit DLLs");
        ReadRegistryValue(
            items,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            "Shell",
            "Winlogon Shell");
        ReadRegistryValue(
            items,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            "Userinit",
            "Winlogon Userinit");

        ReadAppCertDlls(items);
        var ifeo = ReadIfeoDebuggers(items);

        return new WindowsEvidenceReport(
            "Registry",
            items.Count == 0 ? "NO_DATA" : "OK",
            ifeo.Partial,
            items.Count(item => item.Status == "ATTENTION"),
            items);
    }

    public WindowsEvidenceReport AnalyzeCom()
    {
        var items = new List<WindowsEvidenceItem>();
        var partial = false;

        foreach (var view in new[]
                 {
                     RegistryView.Registry64,
                     RegistryView.Registry32
                 })
        {
            var scan = ReadComView(view, items);
            partial |= scan.Partial;
        }

        return new WindowsEvidenceReport(
            "COM",
            items.Count == 0 ? "NO_FINDINGS" : "OK",
            partial,
            items.Count(item => item.Status == "MISSING_TARGET"),
            items
                .OrderByDescending(item =>
                    item.Status == "MISSING_TARGET")
                .ThenBy(item => item.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToArray());
    }

    public WindowsEvidenceReport AnalyzeCertificates()
    {
        var items = new List<WindowsEvidenceItem>();
        var storesRead = 0;

        ReadCertificateStore(
            items,
            StoreLocation.CurrentUser,
            "CurrentUser",
            ref storesRead);
        ReadCertificateStore(
            items,
            StoreLocation.LocalMachine,
            "LocalMachine",
            ref storesRead);

        return new WindowsEvidenceReport(
            "Certificates",
            storesRead == 0 ? "NO_DATA" : "OK",
            false,
            items.Count(item =>
                item.Status is "EXPIRED" or "NOT_YET_VALID"),
            items
                .OrderBy(item => CertificateRank(item.Status))
                .ThenBy(item => item.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Take(MaxCertificateItems)
                .ToArray());
    }

    private static void ReadRegistryValue(
        ICollection<WindowsEvidenceItem> items,
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName,
        string label)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
                return;

            var raw = key.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null)
                return;

            var value = Convert.ToString(raw) ?? string.Empty;
            var status = label switch
            {
                "Winlogon Shell" when
                    value.Equals(
                        "explorer.exe",
                        StringComparison.OrdinalIgnoreCase) => "EXPECTED",
                "Winlogon Userinit" when
                    value.Contains(
                        "userinit.exe",
                        StringComparison.OrdinalIgnoreCase) => "EXPECTED",
                "Load AppInit DLLs" when value is "0" or "" => "EXPECTED",
                "AppInit DLLs" when string.IsNullOrWhiteSpace(value) => "EXPECTED",
                _ => "ATTENTION"
            };

            items.Add(new WindowsEvidenceItem(
                "Registry",
                label,
                status,
                $"{hive} · {view} · {subKey} · {valueName}",
                Compact(value)));
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
        }
    }

    private static void ReadAppCertDlls(
        ICollection<WindowsEvidenceItem> items)
    {
        const string subKey =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
                return;

            foreach (var name in key.GetValueNames().Take(32))
            {
                var value = Convert.ToString(
                    key.GetValue(
                        name,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames));
                items.Add(new WindowsEvidenceItem(
                    "Registry",
                    "AppCertDll · " + name,
                    "ATTENTION",
                    @"HKLM\" + subKey,
                    Compact(value ?? string.Empty)));
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
        }
    }

    private static (bool Partial, int Scanned) ReadIfeoDebuggers(
        ICollection<WindowsEvidenceItem> items)
    {
        const string subKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        var scanned = 0;
        var partial = false;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
                return (false, 0);

            var names = key.GetSubKeyNames();
            partial = names.Length > MaxIfeoEntries;

            foreach (var name in names.Take(MaxIfeoEntries))
            {
                scanned++;
                try
                {
                    using var child = key.OpenSubKey(name);
                    var debugger = Convert.ToString(
                        child?.GetValue(
                            "Debugger",
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames));
                    if (string.IsNullOrWhiteSpace(debugger))
                        continue;

                    items.Add(new WindowsEvidenceItem(
                        "Registry",
                        "IFEO Debugger · " + name,
                        "ATTENTION",
                        @"HKLM\" + subKey + "\" + name,
                        Compact(debugger)));
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
        }

        return (partial, scanned);
    }

    private static (bool Partial, int Scanned) ReadComView(
        RegistryView view,
        ICollection<WindowsEvidenceItem> items)
    {
        var scanned = 0;
        var partial = false;

        try
        {
            using var classes = RegistryKey.OpenBaseKey(
                RegistryHive.ClassesRoot,
                view);
            using var clsid = classes.OpenSubKey("CLSID");
            if (clsid is null)
                return (false, 0);

            var names = clsid.GetSubKeyNames();
            partial = names.Length > MaxComEntriesPerView;

            foreach (var name in names.Take(MaxComEntriesPerView))
            {
                scanned++;
                try
                {
                    using var classKey = clsid.OpenSubKey(name);
                    if (classKey is null)
                        continue;

                    CheckComServer(
                        items,
                        view,
                        name,
                        classKey,
                        "InprocServer32");
                    CheckComServer(
                        items,
                        view,
                        name,
                        classKey,
                        "LocalServer32");
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
        }

        return (partial, scanned);
    }

    private static void CheckComServer(
        ICollection<WindowsEvidenceItem> items,
        RegistryView view,
        string clsid,
        RegistryKey classKey,
        string serverKey)
    {
        using var server = classKey.OpenSubKey(serverKey);
        if (server is null)
            return;

        var raw = Convert.ToString(
            server.GetValue(
                null,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames));
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var resolved = ResolveServerPath(raw);
        if (resolved is null)
            return;

        var exists = File.Exists(resolved);
        if (exists)
            return;

        items.Add(new WindowsEvidenceItem(
            "COM",
            clsid + " · " + serverKey,
            "MISSING_TARGET",
            $"HKCR\CLSID\{clsid}\{serverKey} · {view}",
            Compact(resolved)));
    }

    private static string? ResolveServerPath(string raw)
    {
        var value = Environment
            .ExpandEnvironmentVariables(raw.Trim());

        string candidate;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
                return null;
            candidate = value[1..end];
        }
        else
        {
            var dll = value.IndexOf(
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            var exe = value.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            var end = new[] { dll >= 0 ? dll + 4 : -1, exe >= 0 ? exe + 4 : -1 }
                .Where(index => index > 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (end <= 0)
                return null;
            candidate = value[..end];
        }

        if (!Path.IsPathFullyQualified(candidate))
            return null;

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static void ReadCertificateStore(
        ICollection<WindowsEvidenceItem> items,
        StoreLocation location,
        string locationLabel,
        ref int storesRead)
    {
        try
        {
            using var store = new X509Store(
                StoreName.My,
                location);
            store.Open(
                OpenFlags.ReadOnly |
                OpenFlags.OpenExistingOnly);
            storesRead++;

            var now = DateTime.Now;
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    string status;
                    if (certificate.NotAfter < now)
                        status = "EXPIRED";
                    else if (certificate.NotBefore > now)
                        status = "NOT_YET_VALID";
                    else if (certificate.NotAfter <= now.AddDays(30))
                        status = "EXPIRING_SOON";
                    else
                        continue;

                    items.Add(new WindowsEvidenceItem(
                        "Certificate",
                        CompactSubject(certificate.Subject),
                        status,
                        locationLabel + @"\My",
                        $"Válido {certificate.NotBefore:yyyy-MM-dd} → {certificate.NotAfter:yyyy-MM-dd} · thumbprint …{Tail(certificate.Thumbprint, 10)}"));
                }
            }
        }
        catch (Exception ex) when (ex is
            CryptographicException or
            SecurityException)
        {
        }
    }

    private static int CertificateRank(string status) =>
        status switch
        {
            "EXPIRED" => 0,
            "NOT_YET_VALID" => 1,
            "EXPIRING_SOON" => 2,
            _ => 3
        };

    private static string CompactSubject(string value)
    {
        var cn = value
            .Split(',')
            .Select(part => part.Trim())
            .FirstOrDefault(part =>
                part.StartsWith(
                    "CN=",
                    StringComparison.OrdinalIgnoreCase));
        return Compact(cn ?? value);
    }

    private static string Tail(string? value, int count)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        return value.Length <= count
            ? value
            : value[^count..];
    }

    private static string Compact(string value)
    {
        var compact = string.Join(
            " ",
            value
                .Split(
                    ['\r', '\n', '\t'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        return compact.Length <= 320
            ? compact
            : compact[..317] + "...";
    }
}

public sealed record WindowsEvidenceItem(
    string Category,
    string Name,
    string Status,
    string Source,
    string Detail);

public sealed record WindowsEvidenceReport(
    string Area,
    string Status,
    bool Partial,
    int AttentionCount,
    IReadOnlyList<WindowsEvidenceItem> Items);
