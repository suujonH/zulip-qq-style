using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 表情：Unicode 表情数据来自服务器的 <c>server_emoji_data_url</c>（缓存到本地，地址变化时重新下载），
/// 组织自定义表情来自 register 和 realm_emoji 事件。常用表情（最近使用的 16 个）存在账号设置中。只在 UI 线程上使用。
/// </summary>
public sealed class EmojiService(ZulipSession session, SettingRepository settings)
{
    private const string RecentKey = "recent_emoji";
    private const int MaxRecent = 16;

    // 消息右键的默认表情回应（常用不足 6 个时补齐）
    private static readonly string[] DefaultReactions = ["+1", "heart", "joy", "open_mouth", "cry", "pray"];

    private static readonly System.Net.Http.HttpClient External = new() { Timeout = TimeSpan.FromSeconds(30) };

    private IReadOnlyList<EmojiEntry> _unicode = [];
    private IReadOnlyList<EmojiEntry> _realm = [];
    private Task? _loading;

    public EmojiCatalog Catalog { get; private set; } = EmojiCatalog.Empty;

    /// <summary>目录或常用表情变化。</summary>
    public event EventHandler? Changed;

    /// <summary>register 或 realm_emoji 事件：替换组织自定义表情（不含已停用的）。</summary>
    public void LoadRealm(IReadOnlyDictionary<string, RealmEmojiDto> emoji)
    {
        _realm = emoji.Values
            .Where(e => !e.Deactivated)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new EmojiEntry(e.Name, e.Id, EmojiEntry.RealmType, [], null, e.StillUrl ?? e.SourceUrl))
            .ToList();
        Rebuild();
    }

    /// <summary>加载 Unicode 表情数据（只加载一次；失败时下次调用重试）。</summary>
    public Task EnsureLoadedAsync()
    {
        if (_unicode.Count > 0)
            return Task.CompletedTask;
        return _loading ??= LoadUnicodeAsync();
    }

    /// <summary>最近使用的表情，最新的在前。</summary>
    public IReadOnlyList<EmojiEntry> Recent =>
        ReadRecent().Select(Catalog.TryGet).OfType<EmojiEntry>().DistinctBy(e => e.Name).Take(MaxRecent).ToList();

    /// <summary>消息右键菜单中的 6 个快捷表情回应：常用在前，不足时用默认表情补齐。</summary>
    public IReadOnlyList<EmojiEntry> QuickReactions =>
        Recent.Concat(DefaultReactions.Select(Catalog.TryGet).OfType<EmojiEntry>())
            .DistinctBy(e => e.Name)
            .Take(6)
            .ToList();

    /// <summary>记录一次使用（插入到输入框或作为表情回应）。</summary>
    public void AddRecent(EmojiEntry emoji)
    {
        var names = ReadRecent().Where(n => n != emoji.Name).Prepend(emoji.Name).Take(MaxRecent).ToList();
        settings.Set(RecentKey, JsonSerializer.Serialize(names));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<string> ReadRecent()
    {
        try
        {
            return settings.Get(RecentKey) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task LoadUnicodeAsync()
    {
        try
        {
            if (session.Realm.ServerEmojiDataUrl is not { Length: > 0 } url)
                return;
            var json = await ReadOrDownloadAsync(url);
            if (session.IsDisposed || json is null)
                return;
            _unicode = EmojiCatalog.ParseUnicode(json);
            Rebuild();
        }
        catch (Exception ex)
        {
            if (!session.IsDisposed)
                Log.Warn("加载表情数据失败", ex);
        }
        finally
        {
            _loading = null;
        }
    }

    // 缓存文件旁边记录来源地址；地址变化（服务器升级）时重新下载
    private async Task<string?> ReadOrDownloadAsync(string url)
    {
        var dataPath = Path.Combine(AppPaths.CacheDir, "emoji-data.json");
        var urlPath = dataPath + ".url";
        if (File.Exists(dataPath) && File.Exists(urlPath) && await File.ReadAllTextAsync(urlPath) == url)
            return await File.ReadAllTextAsync(dataPath);

        if (!Uri.TryCreate(session.Client.RealmUrl, url, out var absolute) || absolute.Scheme is not ("http" or "https"))
            return null;
        string json;
        if (absolute.Host == session.Client.RealmUrl.Host)
        {
            using var buffer = new MemoryStream();
            await session.Client.DownloadAsync(absolute.PathAndQuery, buffer, ct: session.Token);
            json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        else
        {
            // 数据放在单独的静态资源主机（CDN）上：不带认证下载
            json = await External.GetStringAsync(absolute, session.Token);
        }
        if (session.IsDisposed)
            return null;
        Directory.CreateDirectory(AppPaths.CacheDir);
        await File.WriteAllTextAsync(dataPath, json);
        await File.WriteAllTextAsync(urlPath, url);
        return json;
    }

    private void Rebuild()
    {
        Catalog = new EmojiCatalog(_realm, _unicode);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
