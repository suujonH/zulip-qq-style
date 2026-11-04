using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

// 联系人页的置顶与消息列表的置顶分开保存：一个表示人 / 群重要，一个表示会话重要
public sealed class ContactStorePinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqpin").FullName;
    private readonly LocalDb _db;
    private long _now = 1000;

    public ContactStorePinTests()
    {
        _db = LocalDb.Open(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // 置顶不使用用户列表
    private ContactStore Store()
    {
        var store = new ContactStore(new ContactRepository(_db, 1), null!) { Now = () => _now };
        store.Load();
        return store;
    }

    [Fact]
    public void Pins_LastPinnedFirst_AndPersist()
    {
        var store = Store();
        store.SetFriendPinned(5, true);
        store.SetFriendPinned(6, true); // 同一毫秒：后置顶的仍在前
        _now = 2000;
        store.SetTopicPinned(new TopicKey(3, "A"), true);
        store.SetTopicPinned(new TopicKey(4, "B"), true);

        var reloaded = Store();
        Assert.Equal([6L, 5L], reloaded.PinnedFriends());
        Assert.Equal([new TopicKey(4, "b"), new TopicKey(3, "a")], reloaded.PinnedTopics());
        Assert.True(reloaded.IsTopicPinned(new TopicKey(3, "a")));
        Assert.True(reloaded.IsFriendPinned(5));
        Assert.False(reloaded.IsFriendPinned(3));

        reloaded.SetFriendPinned(5, false);
        reloaded.SetTopicPinned(new TopicKey(3, "A"), false);
        Assert.Equal([6L], Store().PinnedFriends());
        Assert.Equal([new TopicKey(4, "B")], Store().PinnedTopics());
    }

    [Fact]
    public void Pins_DoNotTouchConversationPins()
    {
        var conv = new ConversationStateRepository(_db, 1);
        var topic = new TopicKey(3, "A");
        conv.SetPinned(new DmKey([5]), 50);

        var store = Store();
        store.SetTopicPinned(topic, true);
        store.SetFriendPinned(5, false);

        Assert.Null(conv.Find(topic));
        Assert.Equal(50, conv.Find(new DmKey([5]))!.PinnedAt);
        Assert.False(store.IsFriendPinned(5));
    }

    [Fact]
    public void Changed_OnlyWhenStateChanges()
    {
        var store = Store();
        var count = 0;
        store.Changed += (_, _) => count++;
        store.SetFriendPinned(5, true);
        store.SetFriendPinned(5, true);
        store.SetFriendPinned(7, false);
        Assert.Equal(1, count);
    }

    [Fact]
    public void RekeyTopic_MovesPin()
    {
        var store = Store();
        store.SetTopicPinned(new TopicKey(3, "old"), true);
        store.RekeyTopic(new TopicKey(3, "old"), new TopicKey(4, "new"));

        var reloaded = Store();
        Assert.Equal([new TopicKey(4, "new")], reloaded.PinnedTopics());
        Assert.False(reloaded.IsTopicPinned(new TopicKey(3, "old")));
    }
}
