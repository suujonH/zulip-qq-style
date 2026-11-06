using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

public sealed class ImageCacheTests : IDisposable
{
    private static readonly byte[] Body = [1, 2, 3, 4];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ZulipQQ.Tests", Guid.NewGuid().ToString("N"));
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private int _requests;

    public ImageCacheTests()
    {
        // 找一个空闲端口：HttpListener 不能绑定端口 0
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _url = $"http://localhost:{port}/avatar.png";
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var context = await _listener.GetContextAsync();
                Interlocked.Increment(ref _requests);
                await Task.Delay(100); // 让并发的请求重叠
                await context.Response.OutputStream.WriteAsync(Body);
                context.Response.Close();
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // 测试结束，监听已停止
        }
    }

    private string CacheFile() =>
        Path.Combine(_dir, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(_url))));

    [Fact]
    public async Task Concurrent_loads_of_one_url_download_once_and_all_succeed()
    {
        var cache = new ImageCache(_dir);

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetFileAsync(_url, null)));

        Assert.All(paths, p => Assert.Equal(CacheFile(), p));
        Assert.Equal(1, _requests);
        Assert.Equal(Body, await File.ReadAllBytesAsync(CacheFile(), TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Finished_download_is_not_kept_as_in_flight()
    {
        var cache = new ImageCache(_dir);
        await cache.GetFileAsync(_url, null);
        File.Delete(CacheFile());

        Assert.Equal(CacheFile(), await cache.GetFileAsync(_url, null));

        Assert.Equal(2, _requests);
    }

    [Fact]
    public async Task Failing_to_store_the_cache_file_leaves_no_temp_file()
    {
        // 缓存文件被其他程序占用（杀毒软件等）：改名失败。缓存已过期，所以会重新下载
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(CacheFile(), [9], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(CacheFile(), DateTime.UtcNow.AddDays(-2));
        var cache = new ImageCache(_dir);

        string? path;
        await using (new FileStream(CacheFile(), FileMode.Open, FileAccess.Read, FileShare.Read))
            path = await cache.GetFileAsync(_url, null);

        Assert.Equal(CacheFile(), path);
        Assert.Equal(1, _requests);
        Assert.Equal([CacheFile()], Directory.GetFiles(_dir));
        Assert.Equal([9], await File.ReadAllBytesAsync(CacheFile(), TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        _listener.Close();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
