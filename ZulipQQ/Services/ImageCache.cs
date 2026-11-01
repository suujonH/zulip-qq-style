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
/// 组织内的地址（相对路径或同一主机）用调用方传入的会话客户端带认证下载，其它地址用普通 HttpClient。
/// 多个账号同时在线时，相对路径以传入的客户端所在的组织为基准，缓存键是解析后的完整地址。
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

    public static ImageCache Shared { get; } = new();

    /// <summary>已在内存缓存中的图片；没有时返回 null，不发起下载。<paramref name="client"/> 是图片所属账号的客户端。</summary>
    public Bitmap? TryGet(string url, int? decodeWidth, ZulipClient? client)
    {
        if (Absolute(url, client) is not { } absolute || !_map.TryGetValue((absolute.AbsoluteUri, decodeWidth ?? 0), out var node))
            return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Bitmap;
    }

    /// <summary>加载图片；<paramref name="client"/> 是图片所属账号的客户端（为 null 时无法解析相对路径，也不带认证）。</summary>
    public Task<Bitmap?> GetAsync(string url, int? decodeWidth, ZulipClient? client)
    {
        if (TryGet(url, decodeWidth, client) is { } cached)
            return Task.FromResult<Bitmap?>(cached);
        if (Absolute(url, client) is not { } absolute)
            return Task.FromResult<Bitmap?>(null);
        var key = (absolute.AbsoluteUri, decodeWidth ?? 0);
        if (_inflight.TryGetValue(key, out var running))
            return running;
        var task = LoadAsync(absolute, decodeWidth, client);
        _inflight[key] = task;
        return task;
    }

    // 账号退出后客户端被释放，进行中的下载失败返回 null
    private async Task<Bitmap?> LoadAsync(Uri absolute, int? decodeWidth, ZulipClient? client)
    {
        var key = (absolute.AbsoluteUri, decodeWidth ?? 0);
        try
        {
            var bytes = await Task.Run(() => ReadOrDownloadAsync(absolute, client, CancellationToken.None));
            if (bytes is null)
                return null;
            var bitmap = await Task.Run(() => Decode(bytes, decodeWidth));
            if (bitmap is null)
                return null;
            Add(key, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug($"加载图片失败：{absolute}", ex);
            return null;
        }
        finally
        {
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

    // 相对路径以客户端所在的组织为基准；没有客户端时无法解析相对路径
    private static Uri? Absolute(string url, ZulipClient? client)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute;
        return client is not null && Uri.TryCreate(client.RealmUrl, url, out var relative) ? relative : null;
    }

    private static bool IsSameHost(Uri url, Uri realm) =>
        string.Equals(url.Host, realm.Host, StringComparison.OrdinalIgnoreCase) && url.Port == realm.Port;
}
