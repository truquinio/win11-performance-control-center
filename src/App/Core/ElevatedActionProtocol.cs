namespace Win11PerformanceControlCenter.App.Core;

public static class ElevatedActionProtocol
{
    public const string ActionFlag = "--elevated-action";
    public const string TokenFlag = "--result-token";
    public const string ParametersFlag = "--parameters-json-b64";
    public const int MaxEncodedParametersLength = 24 * 1024;
    private const string PipePrefix = "WPCC-Elevated-";

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string actionId,
        out Guid token) =>
        TryParse(args, out actionId, out token, out _);

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string actionId,
        out Guid token,
        out string? parametersBase64)
    {
        actionId = string.Empty;
        token = Guid.Empty;
        parametersBase64 = null;

        if (args.Count is not (4 or 6) ||
            !string.Equals(args[0], ActionFlag, StringComparison.Ordinal) ||
            !string.Equals(args[2], TokenFlag, StringComparison.Ordinal))
        {
            return false;
        }

        if (args.Count == 6)
        {
            if (!string.Equals(
                    args[4],
                    ParametersFlag,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[5]) ||
                args[5].Length > MaxEncodedParametersLength)
            {
                return false;
            }

            parametersBase64 = args[5];
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
