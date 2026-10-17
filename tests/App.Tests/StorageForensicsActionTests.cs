using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

public sealed class StorageForensicsActionTests
{
    [Fact]
    public void DuplicateAudit_IsReadOnlyAllowlisted()
    {
        var action = new ActionCatalog().GetRequired("disk.duplicates.scan");
        Assert.Equal(ActionMode.READ, action.Mode);
        Assert.Equal(ActionRisk.SAFE, action.Risk);
        Assert.False(action.RequiresAdmin);
    }
}
