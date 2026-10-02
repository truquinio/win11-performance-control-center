namespace Win11PerformanceControlCenter.App.Core;

public static class ElevatedActionProtocol
{
    public const string ActionFlag = "--elevated-action";
    public const string TokenFlag = "--result-token";
    private const string PipePrefix = "WPCC-Elevated-";

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string actionId,
        out Guid token)
    {
        actionId = string.Empty;
        token = Guid.Empty;

        if (args.Count != 4 ||
            !string.Equals(args[0], ActionFlag, StringComparison.Ordinal) ||
            !string.Equals(args[2], TokenFlag, StringComparison.Ordinal))
        {
            return false;
        }

        actionId = args[1];
        return !string.IsNullOrWhiteSpace(actionId) &&
               Guid.TryParseExact(args[3], "N", out token);
    }

    public static string GetPipeName(Guid token)
    {
        if (token == Guid.Empty)
            throw new ArgumentException(
                "Result token inválido.",
                nameof(token));

        return PipePrefix + token.ToString("N");
    }
}
