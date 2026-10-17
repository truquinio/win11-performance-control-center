using System.Text.Json;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageRecycleJournalTests
{
    [Fact]
    public void Journal_WritesIntentAndMoveWithOriginalPath_ForManualRestore()
    {
        using var fixture = new TestDataRoot();
        var file = Path.Combine(fixture.Path, "state", "recycle.jsonl");
        var journal = new StorageRecycleJournal(file);
        var source = Path.Combine(fixture.Path, "cache", "old.tmp");
        journal.Append("op-1", source, 256, "INTENT");
        journal.Append("op-1", source, 256, "MOVED");
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(source, first.RootElement.GetProperty("originalPath").GetString());
        Assert.Equal("INTENT", first.RootElement.GetProperty("status").GetString());
        Assert.Equal("MOVED", second.RootElement.GetProperty("status").GetString());
        Assert.Equal(256, second.RootElement.GetProperty("logicalBytes").GetInt64());
    }
}
