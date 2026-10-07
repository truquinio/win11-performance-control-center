using System.Reflection;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class SafeCleanupPolicyTests
{
    [Fact]
    public void CleanupWrite_IsCautionAndRequiresExplicitConfirmation()
    {
        var action = new ActionCatalog().GetRequired("disk.cleanup.execute");
        Assert.Equal(ActionMode.WRITE, action.Mode);
        Assert.Equal(ActionRisk.CAUTION, action.Risk);
        Assert.False(action.RequiresAdmin);
        Assert.Contains(action.Parameters!, x =>
            x.Name == "confirmed" && x.Required &&
            x.Type == ActionParameterType.BOOLEAN);
    }

    [Fact]
    public void HibernationReduction_RequiresUacAndIsNotImplicit()
    {
        var catalog = new ActionCatalog();
        Assert.Equal(ActionMode.READ, catalog.GetRequired("disk.hibernate.status").Mode);
        var reduce = catalog.GetRequired("disk.hibernate.reduce");
        Assert.Equal(ActionRisk.CAUTION, reduce.Risk);
        Assert.True(reduce.RequiresAdmin);
        Assert.Equal(ActionMode.WRITE, reduce.Mode);
    }

    [Fact]
    public void CacheAllowlist_ExcludesSensitiveAppStateAndRuntimeDirectories()
    {
        var method = typeof(StorageAnalysisService).GetMethod(
            "Targets", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var targets = ((System.Collections.IEnumerable)method!.Invoke(null, null)!).Cast<object>();
        var paths = targets.Select(x => x.GetType().GetProperty("Path")!
            .GetValue(x)!.ToString()!).ToArray();
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            Assert.DoesNotContain("Claude Extensions", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WhatsApp", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WinGet", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Local\\Programs", path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspaceStorage", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Local Storage", path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
