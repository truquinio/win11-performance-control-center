using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public static class ReliabilityEvidenceClassifier
{
    public static bool TryClassify(
        string provider,
        int eventId,
        out EvidenceClassification classification)
    {
        if (provider.Equals(
                "Microsoft-Windows-Kernel-Power",
                StringComparison.OrdinalIgnoreCase) &&
            eventId is 41 or 42 or 109)
        {
            classification = eventId == 41
                ? EvidenceClassification.INDICIO
                : EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Microsoft-Windows-Kernel-General",
                StringComparison.OrdinalIgnoreCase) &&
            eventId is 12 or 13)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase) &&
            eventId == 6008)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals("User32", StringComparison.OrdinalIgnoreCase) &&
            eventId == 1074)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Microsoft-Windows-WER-SystemErrorReporting",
                StringComparison.OrdinalIgnoreCase) &&
            eventId == 1001)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Microsoft-Windows-WHEA-Logger",
                StringComparison.OrdinalIgnoreCase) &&
            eventId is 1 or 17 or 18 or 19 or 20 or 46 or 47)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Microsoft-Windows-WindowsUpdateClient",
                StringComparison.OrdinalIgnoreCase) &&
            eventId is 19 or 20)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Microsoft-Windows-Kernel-Boot",
                StringComparison.OrdinalIgnoreCase))
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) &&
            eventId == 1000)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) &&
            eventId == 1002)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        if (provider.Equals(
                "Windows Error Reporting",
                StringComparison.OrdinalIgnoreCase) &&
            eventId == 1001)
        {
            classification = EvidenceClassification.HECHO;
            return true;
        }

        classification = EvidenceClassification.NO_DETERMINADO;
        return false;
    }
}
