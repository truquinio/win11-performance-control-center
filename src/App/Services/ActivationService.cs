using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ActivationService
{
    private const string WindowsApplicationId =
        "55c92734-d682-4d71-983e-d6ec3f16059f";

    public Task<ActivationAnalysis> AnalyzeAsync() =>
        Task.Run(() =>
        {
            try
            {
                var query =
                    "SELECT LicenseStatus, Description, PartialProductKey " +
                    "FROM SoftwareLicensingProduct " +
                    $"WHERE ApplicationID='{WindowsApplicationId}' " +
                    "AND PartialProductKey IS NOT NULL";

                using var searcher = new ManagementObjectSearcher(query);
                var candidates = new List<ActivationAnalysis>();

                using var results = searcher.Get();
                foreach (var raw in results)
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        var licenseStatus = ConvertToInt32(
                            item["LicenseStatus"]);

                        candidates.Add(new ActivationAnalysis(
                            Classify(licenseStatus),
                            licenseStatus,
                            Convert.ToString(item["Description"]),
                            Convert.ToString(item["PartialProductKey"])));
                    }
                }

                return candidates
                    .OrderByDescending(item => item.LicenseStatus == 1)
                    .ThenBy(item => item.LicenseStatus < 0)
                    .FirstOrDefault()
                    ?? Unknown();
            }
            catch (ManagementException)
            {
                return Unknown();
            }
            catch (COMException)
            {
                return Unknown();
            }
            catch (UnauthorizedAccessException)
            {
                return Unknown();
            }
        });

    private static ActivationAnalysis Unknown() =>
        new(
            "UNKNOWN",
            -1,
            null,
            null);

    private static string Classify(int status) => status switch
    {
        1 => "LICENSED",
        0 => "UNLICENSED",
        2 or 3 or 4 or 5 or 6 => "GRACE_OR_NOTIFICATION",
        _ => "UNKNOWN"
    };

    private static int ConvertToInt32(object? value)
    {
        if (value is null) return -1;
        try { return Convert.ToInt32(value); }
        catch (FormatException) { return -1; }
        catch (InvalidCastException) { return -1; }
        catch (OverflowException) { return -1; }
    }
}
