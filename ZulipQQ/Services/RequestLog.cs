using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Data;
using ZulipQQ.Zulip;

namespace ZulipQQ.Services;

/// <summary>一条请求记录。请求发出时加入列表（等待中），收到响应或失败后补全。只在 UI 线程上修改。</summary>
public sealed partial class RequestLogEntry(long id, HttpTraceRequest request) : ObservableObject
{
    public long Id { get; } = id;

    public HttpTraceRequest Request { get; } = request;

    public string TimeText => Request.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>列表中显示的名称：组织地址之后的路径和查询参数。</summary>
    public string Name => Uri.TryCreate(Request.Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : Request.Url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsFailed), nameof(StatusText), nameof(StatusLine), nameof(DurationText),
        nameof(ResponseText))]
    public partial HttpTraceResponse? Response { get; private set; }

    /// <summary>详情中的状态：“200 OK”、“等待中”或失败原因。</summary>
    public string StatusLine => Response switch
    {
        null => "等待中",
        { Status: { } status } r => $"{status.ToString(CultureInfo.InvariantCulture)} {r.Reason}".TrimEnd(),
        { Error: var error } => $"失败：{error}",
    };

    /// <summary>载荷：查询参数和表单数据逐项解码为 “名称: 值”；其它正文原样显示。</summary>
    public string PayloadText => _payloadText ??= BuildPayload();

    /// <summary>响应正文；JSON 格式化后显示。</summary>
    public string ResponseText => Response is null ? "" : _responseText ??= Pretty(Response.Body);

    private string? _payloadText;
    private string? _responseText;

    private string BuildPayload()
    {
        var sb = new StringBuilder();
        if (Uri.TryCreate(Request.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
            AppendPairs(sb, "查询参数", uri.Query[1..]);
        if (Request.Body.Length > 0)
        {
            if (Request.Headers.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                AppendPairs(sb, "表单数据", Request.Body);
            else
                sb.Append("请求正文\n").Append(Request.Body).Append('\n');
        }
        return sb.Length == 0 ? "（没有载荷）" : sb.ToString().TrimEnd('\n');
    }

    private static void AppendPairs(StringBuilder sb, string title, string encoded)
    {
        if (sb.Length > 0)
            sb.Append('\n');
        sb.Append(title).Append('\n');
        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
            sb.Append("  ").Append(name).Append(": ").Append(Pretty(value)).Append('\n');
        }
    }

    // JSON 缩进显示（中文不转义）；不是 JSON 时原样返回
    private static string Pretty(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream,
                       new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                doc.WriteTo(writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public bool IsPending => Response is null;

    /// <summary>没有收到响应，或状态码为 4xx、5xx。</summary>
    public bool IsFailed => Response is { Status: null or >= 400 };

    public string StatusText => Response switch
    {
        null => "等待中",
        { Status: { } status } => status.ToString(CultureInfo.InvariantCulture),
        _ => "失败",
    };

    public string DurationText => Response is { } r ? $"{r.Duration.TotalMilliseconds:0} ms" : "";

    internal void Complete(HttpTraceResponse response) => Response = response;

    /// <summary>网址、请求头、请求正文、响应头、响应正文中是否包含 <paramref name="query"/>（不区分大小写）。</summary>
    public bool Matches(string query)
    {
        return Contains(Request.Url) || Contains(Request.Headers) || Contains(Request.Body)
               || (Response is { } r && (Contains(r.Headers) || Contains(r.Body) || Contains(r.Error)));

        bool Contains(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>
/// 开发者模式的“显示请求”：记录所有 <see cref="ZulipClient"/> 的请求和响应，超出保存条数时删除最早的记录。
/// 只保存在内存中。网络线程上调用 <see cref="Begin"/>、<see cref="End"/>，列表的修改都转到 UI 线程。
/// </summary>
public sealed class RequestLog : IHttpTraceSink
{
    private readonly Dictionary<long, RequestLogEntry> _pending = [];
    private long _nextId;
    private volatile bool _enabled;
    private int _limit = LocalSettings.DefaultRequestLogLimit;

    private RequestLog()
    {
    }

    public static RequestLog Instance { get; } = new();

    /// <summary>按时间先后排列的记录。</summary>
    public ObservableCollection<RequestLogEntry> Entries { get; } = [];

    public bool IsEnabled => _enabled;

    /// <summary>一条记录收到了响应（UI 线程）。搜索结果可能因此变化。</summary>
    public event EventHandler<RequestLogEntry>? EntryCompleted;

    /// <summary>程序启动、数据库打开后调用：开始接收请求记录，并读取程序级的设置。</summary>
    public static void Initialize(LocalDb db)
    {
        ZulipClient.TraceSink = Instance;
        // 只读取程序级设置，账号级的部分不会用到
        var app = new SettingRepository(db, null);
        var settings = new LocalSettings(app, app);
        Instance.Configure(settings.LogRequests, settings.RequestLogLimit);
    }

    /// <summary>设置变化时调用。关闭后不再记录，已有的记录保留。</summary>
    public void Configure(bool enabled, int limit)
    {
        _enabled = enabled;
        Dispatcher.UIThread.Post(() =>
        {
            _limit = limit;
            Trim();
        });
    }

    public void Clear()
    {
        Entries.Clear();
        _pending.Clear();
    }

    public long Begin(HttpTraceRequest request)
    {
        var id = Interlocked.Increment(ref _nextId);
        Dispatcher.UIThread.Post(() =>
        {
            var entry = new RequestLogEntry(id, request);
            _pending[id] = entry;
            Entries.Add(entry);
            Trim();
        });
        return id;
    }

    public void End(long id, HttpTraceResponse response) =>
        Dispatcher.UIThread.Post(() =>
        {
            // 已被清除或因超出条数被删除的记录不再更新
            if (_pending.Remove(id, out var entry))
            {
                entry.Complete(response);
                EntryCompleted?.Invoke(this, entry);
            }
        });

    private void Trim()
    {
        while (_limit != LocalSettings.UnlimitedRequestLogLimit && Entries.Count > _limit)
        {
            _pending.Remove(Entries[0].Id);
            Entries.RemoveAt(0);
        }
    }
}
