using System;
using System.Text.Json;

namespace ZulipQQ.Zulip;

/// <summary>Zulip 返回错误（HTTP 非 2xx、<c>result == "error"</c>，或响应不是 JSON）。</summary>
public sealed class ZulipApiException(int httpStatus, string code, string message, JsonElement? body)
    : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;

    /// <summary>Zulip 的错误码；响应中没有时为 <c>BAD_REQUEST</c>，非 JSON 响应为 <c>HTTP_状态码</c>。</summary>
    public string Code { get; } = string.IsNullOrEmpty(code) ? "BAD_REQUEST" : code;

    public JsonElement? Body { get; } = body;

    public bool IsBadEventQueue => Code == "BAD_EVENT_QUEUE_ID";

    public bool IsAuthError => HttpStatus == 401;
}
