using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

// 频道备注只保存在本地，与用户备注一样经 ContactStore 读写
public sealed class ContactStoreRemarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqremark").FullName;
    private readonly LocalDb _db;

    public ContactStoreRemarkTests()
    {
        _db = LocalDb.Open(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // 备注不使用用户列表
    private ContactStore Store(long accountId = 1)
    {
        var store = new ContactStore(new ContactRepository(_db, accountId), null!);
        store.Load();
        return store;
    }

    [Fact]
    public void ChannelRemark_SetChangeClear_AndPersist()
    {
        var store = Store();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        Assert.True(store.SetChannelRemark(3, "  后端  "));
        Assert.Equal("后端", store.GetChannelRemark(3));
        // 相同备注（去掉空白后）不算变化
        Assert.False(store.SetChannelRemark(3, "后端"));
        Assert.Equal(1, changed);

        Assert.Equal("后端", Store().GetChannelRemark(3));
        // 按账号分开保存
        Assert.Null(Store(2).GetChannelRemark(3));

        Assert.True(store.SetChannelRemark(3, "后端组"));
        Assert.Equal("后端组", Store().GetChannelRemark(3));

        // 空白视为清除
        Assert.True(store.SetChannelRemark(3, " "));
        Assert.Null(store.GetChannelRemark(3));
        Assert.Null(Store().GetChannelRemark(3));
        Assert.Equal(3, changed);
    }

    [Fact]
    public void ChannelAndUserRemarks_AreSeparate()
    {
        var store = Store();
        store.SetRemark(3, "小三");
        store.SetChannelRemark(3, "频道三");
        var loaded = Store();
        Assert.Equal("小三", loaded.GetRemark(3));
        Assert.Equal("频道三", loaded.GetChannelRemark(3));
    }
}
