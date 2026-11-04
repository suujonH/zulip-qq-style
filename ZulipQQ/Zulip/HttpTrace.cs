using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZulipQQ.Zulip;

/// <summary>记录下来的请求。头部每行一个 “名称: 值”；正文为文本，或无法记录时的说明（如 “[image/png，1024 字节]”）。</summary>
public sealed record HttpTraceRequest(DateTime Time, string Method, string Url, string Headers, string Body);

/// <summary>记录下来的响应；<see cref="Status"/> 为 null 表示没有收到响应（<see cref="Error"/> 为原因）。</summary>
public sealed record HttpTraceResponse(int? Status, string Reason, string Headers, string Body, string? Error, TimeSpan Duration);

/// <summary>请求记录的接收方（开发者模式的“显示请求”）。可能在任意线程上调用。</summary>
public interface IHttpTraceSink
{
    bool IsEnabled { get; }

    /// <summary>请求发出前调用，返回这条记录的 id。</summary>
    long Begin(HttpTraceRequest request);

    /// <summary>收到响应或请求失败后调用。</summary>
    void End(long id, HttpTraceResponse response);
}

/// <summary>
/// 记录经过 <see cref="ZulipClient"/> 的请求和响应。没有开启记录时直接转发。
/// 认证信息、密码、api_key 打码；只读取 API 请求（路径含 /api/）的文本响应，
/// 文件上传下载、图片等只记录类型和大小，不影响流式读取。
/// </summary>
internal sealed partial class HttpTraceHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string Masked = "******";

    // 记录的正文超过这个长度时截断（register 的响应可能有几 MB）
    private const int MaxBodyChars = 2 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // 客户端名称可以在设置中修改：每个请求都用当前的值（不放在 DefaultRequestHeaders 中）
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", ZulipClient.UserAgent);
        if (ZulipClient.TraceSink is not { IsEnabled: true } sink)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        var id = sink.Begin(new HttpTraceRequest(DateTime.Now, request.Method.Method, request.RequestUri?.ToString() ?? "",
            FormatHeaders(request.Headers, request.Content?.Headers), await RequestBodyAsync(request.Content, ct).ConfigureAwait(false)));
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sink.End(id, new HttpTraceResponse(null, "", "", "", ex is OperationCanceledException ? "已取消或超时" : ex.Message,
                Stopwatch.GetElapsedTime(started)));
            throw;
        }

        string body;
        try
        {
            body = await ResponseBodyAsync(request, response.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            body = $"[读取响应失败：{ex.Message}]";
        }
        sink.End(id, new HttpTraceResponse((int)response.StatusCode, response.ReasonPhrase ?? "",
            FormatHeaders(response.Headers, response.Content.Headers), body, null, Stopwatch.GetElapsedTime(started)));
        return response;
    }

    private static string FormatHeaders(HttpHeaders headers, HttpContentHeaders? content)
    {
        var sb = new StringBuilder();
        foreach (var (name, values) in content is null ? headers : headers.Concat(content))
        {
            var value = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? (values.FirstOrDefault()?.Split(' ')[0] ?? "") + " " + Masked
                : string.Join(", ", values);
            sb.Append(name).Append(": ").Append(value).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // 表单可以重复读取，读出后打码；其它（multipart 上传）只记录类型和大小
    private static async Task<string> RequestBodyAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null)
            return "";
        if (content is FormUrlEncodedContent or StringContent)
            return MaskForm(await content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Describe(content);
    }

    private static async Task<string> ResponseBodyAsync(HttpRequestMessage request, HttpContent content, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (!path.Contains("/api/", StringComparison.Ordinal) || !IsText(content.Headers.ContentType?.MediaType))
            return Describe(content);
        // API 的响应调用方本来就会整个读出；读入缓冲区后调用方仍然可以读取正文
        await content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
        var text = MaskJson(await content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return text.Length <= MaxBodyChars ? text : text[..MaxBodyChars] + $"\n[已截断，共 {text.Length.ToString(CultureInfo.InvariantCulture)} 字符]";
    }

    private static bool IsText(string? mediaType) => mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

    private static string Describe(HttpContent content)
    {
        var type = content.Headers.ContentType?.MediaType ?? "未知类型";
        return content.Headers.ContentLength is { } length
            ? $"[{type}，{length.ToString(CultureInfo.InvariantCulture)} 字节，未记录内容]"
            : $"[{type}，未记录内容]";
    }

    private static string MaskForm(string form) => FormSecretPattern().Replace(form, m => m.Groups[1].Value + Masked);

    private static string MaskJson(string json) => JsonSecretPattern().Replace(json, m => m.Groups[1].Value + Masked + "\"");

    [GeneratedRegex("((?:^|&)(?:password|api_key)=)[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex FormSecretPattern();

    [GeneratedRegex("(\"api_key\"\\s*:\\s*\")[^\"]*\"")]
    private static partial Regex JsonSecretPattern();
}
