using ZulipQQ.Core;
using ZulipQQ.Data;

namespace ZulipQQ.Tests.Data;

public sealed class LocalDbTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqdb").FullName;
    private readonly LocalDb _db;

    public LocalDbTests()
    {
        _db = LocalDb.Open(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Migration_SetsVersion_AndIsIdempotent()
    {
        Assert.Equal(LocalDb.SchemaVersion, _db.UserVersion);
        _db.Dispose();
        using var again = LocalDb.Open(Path.Combine(_dir, "test.db"));
        Assert.Equal(LocalDb.SchemaVersion, again.UserVersion);
    }

    [Fact]
    public void Account_RoundTrip()
    {
        var repo = new AccountRepository(_db);
        var key = AccountRepository.ProtectKey("secret-key");
        var id = repo.Upsert(new AccountRow(0, "https://a/", "x@a", 0, "", null, key, true, true, 100));
        repo.Upsert(new AccountRow(0, "https://b/", "y@b", 0, "", null, key, false, false, 200));

        var all = repo.GetAll();
        Assert.Equal(["y@b", "x@a"], all.Select(a => a.Email));
        Assert.Equal("secret-key", AccountRepository.UnprotectKey(repo.Find("https://a/", "x@a")!.ApiKey));

        repo.UpdateProfile(id, 8, "群主", "/avatar.png", 300);
        repo.SetAutoLogin(id, false);
        var row = repo.Get(id)!;
        Assert.Equal((8L, "群主", "/avatar.png", false, 300L), (row.UserId, row.FullName, row.AvatarUrl, row.AutoLogin, row.LastLoginAt));

        // 重复登录同一账号更新原行
        Assert.Equal(id, repo.Upsert(row with { FullName = "新名字" }));
        Assert.Equal(2, repo.GetAll().Count);

        repo.Delete(id);
        Assert.Null(repo.Get(id));
    }

    [Fact]
    public void ConversationState_PreviewOnlyMovesForward()
    {
        var repo = new ConversationStateRepository(_db, 1);
        var key = new TopicKey(4, "Build");
        repo.UpdatePreview(key, 10, 1000, 9, "a", "Build");
        repo.UpdatePreview(key, 5, 900, 9, "old", "build");
        var row = repo.Find(key)!;
        Assert.Equal((10L, "a", "Build", false), (row.LastMessageId, row.LastPreview, row.TitleCache, row.InList));

        repo.ReplacePreview(key, 0, 0, 0, "");
        Assert.Equal(0, repo.Find(key)!.LastMessageId);
    }

    [Fact]
    public void ConversationState_Setters()
    {
        var repo = new ConversationStateRepository(_db, 1);
        var key = DmKey.Create([9, 10], 8);
        repo.SetPinned(key, 123);
        repo.SetDnd(key, DndLevel.Assistant);
        repo.SetInList(key, true);
        repo.SetRemark(key, "备注");
        var row = Assert.Single(repo.LoadAll());
        Assert.Equal(key, row.Key);
        Assert.Equal((123L, DndLevel.Assistant, true, "备注"), (row.PinnedAt, row.DndLevel, row.InList, row.Remark));

        repo.SetPinned(key, null);
        repo.SetDnd(key, null);
        Assert.Null(repo.Find(key)!.PinnedAt);
        Assert.Null(repo.Find(key)!.DndLevel);

        // 其它账号互不影响
        Assert.Empty(new ConversationStateRepository(_db, 2).LoadAll());
    }

    [Fact]
    public void ConversationState_Rename()
    {
        var repo = new ConversationStateRepository(_db, 1);
        var a = new TopicKey(4, "A");
        repo.UpdatePreview(a, 1, 1, 1, "p", "A");
        repo.SetPinned(a, 5);

        // 只改大小写：只更新标题
        repo.Rename(a, new TopicKey(4, "a"), "a");
        Assert.Equal("a", repo.Find(a)!.TitleCache);

        // 改为新键：整行迁移
        var b = new TopicKey(5, "B");
        repo.Rename(a, b, "B");
        Assert.Null(repo.Find(a));
        Assert.Equal((5L, "B"), (repo.Find(b)!.PinnedAt, repo.Find(b)!.TitleCache));

        // 目标已存在：删除旧行，保留新行
        var c = new TopicKey(6, "C");
        repo.SetRemark(c, "保留");
        repo.Rename(b, c, "C2");
        Assert.Null(repo.Find(b));
        Assert.Equal(("保留", "C2"), (repo.Find(c)!.Remark, repo.Find(c)!.TitleCache));
    }

    [Fact]
    public void Contacts_RoundTrip()
    {
        var repo = new ContactRepository(_db, 1);
        var g1 = repo.AddGroup("同事");
        var g2 = repo.AddGroup("同学");
        Assert.Equal(["同事", "同学"], repo.LoadGroups().Select(g => g.Name));

        repo.SetMembership([9, 10], g1.Id);
        repo.SetMembership([10], g2.Id);
        Assert.Equal(g1.Id, repo.LoadMembership()[9]);
        Assert.Equal(g2.Id, repo.LoadMembership()[10]);

        repo.SetMembership([9], null);
        Assert.False(repo.LoadMembership().ContainsKey(9));

        repo.RenameGroup(g2.Id, "大学同学");
        repo.DeleteGroup(g1.Id);
        Assert.Equal(["大学同学"], repo.LoadGroups().Select(g => g.Name));

        repo.DeleteGroup(g2.Id);
        Assert.Empty(repo.LoadMembership());

        repo.SetRemark(9, "小九");
        Assert.Equal("小九", repo.LoadRemarks()[9]);
        repo.SetRemark(9, null);
        Assert.Empty(repo.LoadRemarks());
    }

    [Fact]
    public void Downloads_RecentOrder()
    {
        var repo = new DownloadRepository(_db, 1);
        repo.Upsert(new DownloadRecord("/user_uploads/a", @"C:\a", "a", 1, 1, "d:9", 100, null));
        repo.Upsert(new DownloadRecord("/user_uploads/b", @"C:\b", "b", 2, 2, "d:9", 200, null));
        repo.MarkOpened("/user_uploads/a", 300);
        Assert.Equal(["/user_uploads/a", "/user_uploads/b"], repo.Recent(10).Select(r => r.UploadPath));
        repo.Delete("/user_uploads/a");
        Assert.Null(repo.Find("/user_uploads/a"));
    }

    [Fact]
    public void Settings_AppAndAccountAreSeparate()
    {
        var app = new SettingRepository(_db, null);
        var account = new SettingRepository(_db, 1);
        app.Set("k", "app");
        account.Set("k", "acc");
        account.Set("k", "acc2");
        Assert.Equal("app", app.Get("k"));
        Assert.Equal("acc2", account.Get("k"));
        Assert.Null(new SettingRepository(_db, 2).Get("k"));
        account.Remove("k");
        Assert.Null(account.Get("k"));
    }

    [Fact]
    public void LocalDraftsAndPlugins()
    {
        var drafts = new LocalDraftRepository(_db, 1);
        drafts.Set(new TopicKey(4, "X"), "草稿", 1);
        Assert.Equal("草稿", drafts.LoadAll()[new TopicKey(4, "x")]);
        drafts.DeleteAll();
        Assert.Empty(drafts.LoadAll());

        var plugins = new PluginStateRepository(_db);
        plugins.SetEnabled("com.a", false);
        plugins.SetEnabled("com.a", true);
        Assert.True(plugins.LoadAll()["com.a"]);
    }
}
