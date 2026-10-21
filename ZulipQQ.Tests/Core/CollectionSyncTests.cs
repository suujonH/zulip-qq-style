using System.Collections.ObjectModel;
using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class CollectionSyncTests
{
    private sealed class Item(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void SyncTo_ReordersRemovesAndInserts_KeepingInstances()
    {
        var a = new Item("a");
        var b = new Item("b");
        var c = new Item("c");
        var d = new Item("d");
        var target = new ObservableCollection<Item> { a, b, c };
        var events = 0;
        target.CollectionChanged += (_, _) => events++;

        CollectionSync.SyncTo(target, [c, a, d]);

        Assert.Equal([c, a, d], target);
        Assert.True(events <= 3);
    }

    [Fact]
    public void SyncTo_Unchanged_RaisesNoEvents()
    {
        var a = new Item("a");
        var target = new ObservableCollection<Item> { a };
        var events = 0;
        target.CollectionChanged += (_, _) => events++;
        CollectionSync.SyncTo(target, [a]);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SyncTo_Empty()
    {
        var target = new ObservableCollection<Item> { new("a"), new("b") };
        CollectionSync.SyncTo(target, []);
        Assert.Empty(target);
    }
}
