using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

public sealed class StorageDirectoryTrendActionTests
{
    [Fact]
    public void DirectoryGrowthAction_IsReadOnlyAndDoesNotRequireElevation()
    {
        var action = new ActionCatalog().GetRequired("disk.directories.watch");
        Assert.Equal(ActionMode.READ, action.Mode);
        Assert.Equal(ActionRisk.SAFE, action.Risk);
        Assert.False(action.RequiresAdmin);
    }
}
