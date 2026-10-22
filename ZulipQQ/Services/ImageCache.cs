using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ZulipQQ.Zulip;

namespace ZulipQQ.Services;

/// <summary>
/// 图片缓存：内存 LRU（按地址 + 解码宽度）+ 磁盘缓存（<c>cache\images\{SHA1(地址)}</c>，保存原始字节）。
/// 组织内的地址（相对路径或同一主机）用当前会话的客户端带认证下载，其它地址用普通 HttpClient。
/// 只在 UI 线程上调用；解码在线程池上进行。失败返回 null。
/// </summary>
public sealed class ImageCache
{
    private const int MemoryCapacity = 300;

    // 组织外的图片（如 Gravatar）换图后地址不变，磁盘缓存超过这个时间就重新下载
    private static readonly TimeSpan ExternalMaxAge = TimeSpan.FromDays(1);

    private static readonly HttpClient External = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Dictionary<(string Url, int Width), LinkedListNode<(string Url, int Width, Bitmap Bitmap)>> _map = [];
    private readonly LinkedList<(string Url, int Width, Bitmap Bitmap)> _lru = [];
    private readonly Dictionary<(string Url, int Width), Task<Bitmap?>> _inflight = [];
    private ZulipClient? _client;
    private CancellationTokenSource _cts = new();

    public static ImageCache Shared { get; } = new();

    /// <summary>会话开始时绑定客户端；结束时传入 null，取消进行中的下载并清空内存缓存。</summary>
    public void Attach(ZulipClient? client)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _client = client;
        _map.Clear();
        _lru.Clear();
        _inflight.Clear();
    }

    /// <summary>已在内存缓存中的图片；没有时返回 null，不发起下载。</summary>
    public Bitmap? TryGet(string url, int? decodeWidth)
    {
        var key = (url, decodeWidth ?? 0);
        if (!_map.TryGetValue(key, out var node))
            return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Bitmap;
    }

    public Task<Bitmap?> GetAsync(string url, int? decodeWidth)
    {
        if (TryGet(url, decodeWidth) is { } cached)
            return Task.FromResult<Bitmap?>(cached);
        var key = (url, decodeWidth ?? 0);
        if (_inflight.TryGetValue(key, out var running))
            return running;
        var task = LoadAsync(url, decodeWidth, _client, _cts.Token);
        _inflight[key] = task;
        return task;
    }

    private async Task<Bitmap?> LoadAsync(string url, int? decodeWidth, ZulipClient? client, CancellationToken ct)
    {
        var key = (url, decodeWidth ?? 0);
        try
        {
            var absolute = Absolute(url, client);
            if (absolute is null)
                return null;
            var bytes = await Task.Run(() => ReadOrDownloadAsync(absolute, client, ct), ct);
            if (bytes is null || ct.IsCancellationRequested)
                return null;
            var bitmap = await Task.Run(() => Decode(bytes, decodeWidth), ct);
            if (bitmap is null || ct.IsCancellationRequested)
                return null;
            Add(key, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"加载图片失败：{url}", ex);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                _inflight.Remove(key);
        }
    }

    private static async Task<byte[]?> ReadOrDownloadAsync(Uri url, ZulipClient? client, CancellationToken ct)
    {
        var path = Path.Combine(AppPaths.ImageCacheDir,
            Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))));
        // 组织内上传的头像和图片地址带内容哈希，可以一直使用磁盘缓存
        var isRealm = client is not null && IsSameHost(url, client.RealmUrl);
        if (File.Exists(path) && (isRealm || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ExternalMaxAge))
            return await File.ReadAllBytesAsync(path, ct);

        byte[] bytes;
        if (isRealm)
        {
            using var buffer = new MemoryStream();
            await client!.DownloadAsync(url.PathAndQuery, buffer, ct: ct);
            bytes = buffer.ToArray();
        }
        else
        {
            bytes = await External.GetByteArrayAsync(url, ct);
        }

        Directory.CreateDirectory(AppPaths.ImageCacheDir);
        // 先写临时文件再改名，避免并发读到写了一半的文件
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        return bytes;
    }

    private static Bitmap? Decode(byte[] bytes, int? decodeWidth)
    {
        using var stream = new MemoryStream(bytes);
        return decodeWidth is > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth.Value) : new Bitmap(stream);
    }

    private void Add((string Url, int Width) key, Bitmap bitmap)
    {
        if (_map.TryGetValue(key, out var old))
            _lru.Remove(old);
        _map[key] = _lru.AddFirst((key.Url, key.Width, bitmap));
        // 淘汰的位图不主动释放：可能仍有控件在显示，交给 GC 回收
        while (_lru.Count > MemoryCapacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove((last.Value.Url, last.Value.Width));
        }
    }

    // 相对路径以当前组织为基准；没有会话时无法解析相对路径
    private static Uri? Absolute(string url, ZulipClient? client)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute;
        return client is not null && Uri.TryCreate(client.RealmUrl, url, out var relative) ? relative : null;
    }

    private static bool IsSameHost(Uri url, Uri realm) =>
        string.Equals(url.Host, realm.Host, StringComparison.OrdinalIgnoreCase) && url.Port == realm.Port;
}
