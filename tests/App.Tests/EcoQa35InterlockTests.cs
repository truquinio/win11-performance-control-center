using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

/// <summary>
/// Eco-QA 35: IPC boundary and consent-policy proof for every catalogued WRITE.
/// Deliberately never submits confirmed=true, so UAC/system WRITE cannot start.
/// </summary>
public sealed class EcoQa35InterlockTests
{
    public static IEnumerable<object[]> AllWriteIds()
    {
        var catalog = new ActionCatalog();
        return catalog.All
            .Where(action => action.Mode == ActionMode.WRITE)
            .OrderBy(action => action.Id, StringComparer.Ordinal)
            .Select(action => new object[] { action.Id })
            .ToArray();
    }

    [Fact]
    public void EcoQaInventory_HasExactly35UniqueWriteScenarios()
    {
        var ids = AllWriteIds()
            .Select(row => Assert.IsType<string>(row[0]))
            .ToArray();

        Assert.Equal(35, ids.Length);
        Assert.Equal(35, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(AllWriteIds))]
    [Trait("Layer", "EcoQa35")]
    public async Task RefusedWrite_NeverStartsOrElevates(string actionId)
    {
        var parameters = SampleParameters(actionId);
        parameters["confirmed"] = false;

        await AssertRejectedWithoutStartAsync(actionId, parameters, "confirmación");
    }

    [Theory]
    [MemberData(nameof(AllWriteIds))]
    [Trait("Layer", "EcoQa35")]
    public async Task MissingConsent_NeverStartsOrElevates(string actionId)
    {
        var parameters = SampleParameters(actionId);
        parameters.Remove("confirmed");

        await AssertRejectedWithoutStartAsync(actionId, parameters, "confirm");
    }

    [Theory]
    [MemberData(nameof(AllWriteIds))]
    [Trait("Layer", "EcoQa35")]
    public async Task InvalidConsentType_NeverStartsOrElevates(string actionId)
    {
        var parameters = SampleParameters(actionId);
        parameters["confirmed"] = "yes";

        await AssertRejectedWithoutStartAsync(actionId, parameters, "Tipo inválido");
    }

    [Theory]
    [MemberData(nameof(AllWriteIds))]
    [Trait("Layer", "EcoQa35")]
    public async Task UnexpectedParameter_NeverStartsOrElevates(string actionId)
    {
        var parameters = SampleParameters(actionId);
        parameters["confirmed"] = false;
        parameters["arbitraryCommand"] = "do-not-execute";

        await AssertRejectedWithoutStartAsync(actionId, parameters, "no permitido");
    }

    private static Dictionary<string, object> SampleParameters(string actionId)
    {
        var action = new ActionCatalog().GetRequired(actionId);
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in action.Parameters ?? [])
        {
            fields[field.Name] = field.Type switch
            {
                ActionParameterType.STRING => "EcoQA-Synthetic-Only",
                ActionParameterType.INTEGER => 2147483000,
                ActionParameterType.INTEGER_ARRAY => new[] { 2147483000 },
                ActionParameterType.BOOLEAN => true,
                _ => throw new InvalidOperationException(
                    "Unsupported parameter type: " + field.Type)
            };
        }

        return fields;
    }

    private static async Task AssertRejectedWithoutStartAsync(
        string actionId,
        Dictionary<string, object> parameters,
        string expectedError)
    {
        using var data = new TestDataRoot();
        using var bridge = data.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "eco-qa-" + Guid.NewGuid().ToString("N"),
            method = "actions.run",
            payload = new { id = actionId, parameters }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok, actionId);
        Assert.Contains(expectedError, response.Error,
            StringComparison.OrdinalIgnoreCase);
        // Reject before beginning the transaction, invoking UAC or writing to
        // any Windows target. Only the bridge REJECTED audit is permitted.
        var log = Path.Combine(data.Path, "Logs", "app.jsonl");
        if (File.Exists(log))
        {
            foreach (var line in File.ReadLines(log))
            {
                using var record = JsonDocument.Parse(line);
                var json = record.RootElement;
                Assert.Equal("bridge", json.GetProperty("actionId").GetString());
                Assert.Equal("REJECTED", json.GetProperty("status").GetString());
            }
        }

        Assert.False(File.Exists(Path.Combine(
            data.Path, "State", "workload-mode.json")));
        Assert.False(File.Exists(Path.Combine(
            data.Path, "State", "pagefile.json")));
    }
}
