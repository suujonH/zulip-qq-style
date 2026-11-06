using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

public sealed class DataLocationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqloc").FullName;
    private readonly LocalDb _db;

    public DataLocationTests()
    {
        _db = LocalDb.Open(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RebasePaths_RewritesDownloadsAndSettings()
    {
        var accounts = new AccountRepository(_db);
        var id = accounts.Upsert(new AccountRow(0, "https://a/", "x@a", 0, "", null, [1], true, true, 100));
        var downloads = new DownloadRepository(_db, id);
        downloads.Upsert(new DownloadRecord("/user_uploads/1/a/b/f.txt", @"C:\Old\ZulipQQ\files\a\f.txt", "f.txt", 1, 1, "t:1:x", 1, null));
        downloads.Upsert(new DownloadRecord("/user_uploads/1/a/c/g.txt", @"D:\Elsewhere\g.txt", "g.txt", 1, 1, "t:1:x", 1, null));
        var settings = new SettingRepository(_db, null);
        // JSON 中的反斜杠是转义过的；前缀相同但不是同一个目录（ZulipQQ2）的不改
        settings.Set("palette", """{"background":"C:\Old\ZulipQQ\theme\bg.png","other":"C:\Old\ZulipQQ2\x"}""");
        settings.Set("plain", @"c:\old\zulipqq\plugin-data\p");

        DataLocation.RebasePaths(_db, @"C:\Old\ZulipQQ", @"E:\New\ZulipQQ");

        Assert.Equal(@"E:\New\ZulipQQ\files\a\f.txt", downloads.Find("/user_uploads/1/a/b/f.txt")!.LocalPath);
        Assert.Equal(@"D:\Elsewhere\g.txt", downloads.Find("/user_uploads/1/a/c/g.txt")!.LocalPath);
        Assert.Equal("""{"background":"E:\New\ZulipQQ\theme\bg.png","other":"C:\Old\ZulipQQ2\x"}""", settings.Get("palette"));
        Assert.Equal(@"E:\New\ZulipQQ\plugin-data\p", settings.Get("plain"));
    }
}
