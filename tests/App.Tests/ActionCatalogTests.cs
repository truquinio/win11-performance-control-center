using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

public sealed class ActionCatalogTests
{
    private readonly ActionCatalog _catalog = new();

    [Fact]
    public void Catalog_HasUniqueIds()
    {
        var ids = _catalog.All.Select(action => action.Id).ToArray();

        Assert.NotEmpty(ids);
        Assert.Equal(
            ids.Length,
            ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetRequired_ReturnsAllowlistedAction()
    {
        var action = _catalog.GetRequired("disk.cleanup.safe");

        Assert.Equal(ActionMode.DRY_RUN, action.Mode);
        Assert.Equal(ActionRisk.SAFE, action.Risk);
        Assert.True(action.Reversible);
    }

    [Fact]
    public void IntegrityCheck_IsReadOnlyAndRequiresElevation()
    {
        var action = _catalog.GetRequired("system.integrity.check");

        Assert.Equal(ActionMode.READ, action.Mode);
        Assert.Equal(ActionRisk.SAFE, action.Risk);
        Assert.True(action.RequiresAdmin);
        Assert.False(action.Reversible);
    }

    [Fact]
    public void WriteActions_HaveTypedRequiredParameters()
    {
        foreach (var id in new[]
                 {
                     "memory.trim",
                     "cpu.ecoqos.apply",
                     "cpu.ecoqos.restore"
                 })
        {
            var action = _catalog.GetRequired(id);

            Assert.Equal(ActionMode.WRITE, action.Mode);
            Assert.NotNull(action.Parameters);
            Assert.Contains(
                action.Parameters!,
                parameter =>
                    parameter.Name == "processIds" &&
                    parameter.Type == ActionParameterType.INTEGER_ARRAY &&
                    parameter.Required);
            Assert.Contains(
                action.Parameters!,
                parameter =>
                    parameter.Name == "confirmed" &&
                    parameter.Type == ActionParameterType.BOOLEAN &&
                    parameter.Required);
        }
    }

    [Theory]
    [InlineData("unknown.action")]
    [InlineData("disk.scan && extra")]
    [InlineData("disk.scan;extra")]
    [InlineData("../disk.scan")]
    public void GetRequired_RejectsNonCatalogIds(string id)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => _catalog.GetRequired(id));

        Assert.Contains("no permitida", error.Message);
    }
}
