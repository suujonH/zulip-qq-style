using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace PluginShared;

/// <summary>
/// 内置 AI 插件共用的宿主调用封装：日志、设置、附加内容、上下文消息，以及一个工作线程（耗时的模型请求不在插件线程上执行）。
/// </summary>
internal static unsafe class Host
{
    private static ZqHostApi* _api;
    private static string _category = "";
    // 回调在调用宿主函数的线程上同步执行：每个线程各自接收
    [ThreadStatic] private static string? _received;
    private static readonly BlockingCollection<Action> Work = new(boundedCapacity: 50);

    /// <summary>zq_plugin_init 中调用：检查版本并保存函数表，启动工作线程。</summary>
    public static bool Init(ZqHostApi* api, string category)
    {
        if (api == null || api->ApiVersion < Zq.ApiVersion || api->StructSize < sizeof(ZqHostApi))
            return false;
        _api = api;
        _category = category;
        new Thread(() =>
        {
            foreach (var action in Work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log(Zq.LogError, $"处理失败：{ex.Message}");
                }
            }
        }) { IsBackground = true, Name = category }.Start();
        return true;
    }

    /// <summary>投递到工作线程；排队过多时返回 false。</summary>
    public static bool Post(Action action) => Work.TryAdd(action);

    public static string? Read(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    public static void Log(int level, string text)
    {
        if (_api == null)
            return;
        WithUtf8(_category, c => WithUtf8(text, t =>
        {
            _api->AddLog(_api->Ctx, level, (byte*)c, (byte*)t);
            return 0;
        }));
    }

    /// <summary>插件设置（宿主保存的 JSON 对象）；读取失败时为空对象。</summary>
    public static JsonObject GetSettings()
    {
        _received = null;
        if (_api->GetSettings(_api->Ctx, &OnString, 0) != Zq.Ok || _received is null)
            return [];
        try
        {
            return JsonNode.Parse(_received) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>插件的数据目录（宿主创建，可读写）；读取失败时为 null。</summary>
    public static string? GetDataDir()
    {
        _received = null;
        return _api->GetDataDir(_api->Ctx, &OnString, 0) == Zq.Ok ? _received : null;
    }

    /// <summary>回调之外调用宿主函数前，切换到消息所属的账号。</summary>
    public static bool SelectAccount(long accountId) => _api->SelectAccount(_api->Ctx, accountId) == Zq.Ok;

    /// <summary>设置附加内容（JSON 格式见头文件 set_annotation）；返回宿主的结果码。</summary>
    public static int SetAnnotation(long messageId, JsonObject annotation) =>
        WithUtf8(annotation.ToJsonString(), p => _api->SetAnnotation(_api->Ctx, messageId, (byte*)p));

    /// <summary>回复设置窗口（任何线程）：<c>{"key", "row", "text", "options"}</c>，见头文件 set_settings_feedback。</summary>
    public static int SetSettingsFeedback(JsonObject feedback) =>
        WithUtf8(feedback.ToJsonString(), p => _api->SetSettingsFeedback(_api->Ctx, (byte*)p));

    /// <summary>在设置项 <paramref name="key"/> 的下方显示文字；<paramref name="row"/> 为 zq_on_settings_button 收到的行号（顶层为 null）。</summary>
    public static int SetSettingsText(string key, long? row, string text)
    {
        var feedback = new JsonObject { ["key"] = key, ["text"] = text };
        if (row is { } r)
            feedback["row"] = r;
        return SetSettingsFeedback(feedback);
    }

    /// <summary>tags 设置项 <paramref name="key"/> 中 <paramref name="item"/> 旁的按钮的结果：显示为成功或失败，失败时 <paramref name="error"/> 为原因。</summary>
    public static int SetSettingsItemResult(string key, long? row, string item, bool ok, string? error = null)
    {
        var feedback = new JsonObject { ["key"] = key, ["item"] = item, ["status"] = ok ? "ok" : "error" };
        if (error is not null)
            feedback["text"] = error;
        if (row is { } r)
            feedback["row"] = r;
        return SetSettingsFeedback(feedback);
    }

    /// <summary>发布资源（任何线程），格式见头文件 publish_resources。</summary>
    public static int PublishResources(string type, JsonArray resources) =>
        WithUtf8(type, t => WithUtf8(resources.ToJsonString(), j => _api->PublishResources(_api->Ctx, (byte*)t, (byte*)j)));

    /// <summary>使用其他插件的资源（工作线程，阻塞到回复或超时）；返回 (结果码, 回复)。</summary>
    public static (int Code, string? Reply) RequestResource(JsonObject request)
    {
        _received = null;
        var code = WithUtf8(request.ToJsonString(), p => _api->RequestResource(_api->Ctx, (byte*)p, &OnString, 0));
        return (code, _received);
    }

    /// <summary>回复 zq_on_resource_request 收到的请求（任何线程）。</summary>
    public static int ReplyResource(long requestId, JsonObject reply) =>
        WithUtf8(reply.ToJsonString(), p => _api->ReplyResource(_api->Ctx, requestId, (byte*)p));

    /// <summary>回复 zq_on_panel（只能在该回调中调用）：<c>{"fields": [...]}</c> 或 <c>{"entries": [...]}</c>，见头文件 reply_panel。</summary>
    public static int ReplyPanel(JsonObject reply) =>
        WithUtf8(reply.ToJsonString(), p => _api->ReplyPanel(_api->Ctx, (byte*)p));

    /// <summary>该消息之前的 <paramref name="count"/> 条消息（不含它），每条一行：“[时间] 名字：内容”，自己发送的标为“我”。</summary>
    public static List<string> GetContext(MessageInfo message, int count) => GetContextMessages(message, count)
        .Select(m => $"[{MessageInfo.FormatTime(m.Timestamp)}] {(m.IsOutgoing ? $"我（{m.SenderName}）" : m.SenderName)}：{m.Text}")
        .ToList();

    /// <summary>该消息之前的 <paramref name="count"/> 条消息（不含它），按时间先后。</summary>
    public static List<ContextMessage> GetContextMessages(MessageInfo message, int count)
    {
        var buffer = new ContextBuffer(message.Id, []);
        if (count <= 0)
            return buffer.Messages;
        using var conv = new NativeConversation(message);
        var handle = GCHandle.Alloc(buffer);
        try
        {
            _api->GetMessages(_api->Ctx, conv.Pointer, message.Id, count, 0, &OnContextMessage, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return buffer.Messages;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextMessage(ZqMessage* msg, nint user)
    {
        var buffer = (ContextBuffer)GCHandle.FromIntPtr(user).Target!;
        // 锚点（这条消息本身）也会返回
        if (msg->MessageId == buffer.Skip)
            return;
        buffer.Messages.Add(new ContextMessage(msg->MessageId, msg->Timestamp, (msg->Flags & Zq.MsgOutgoing) != 0,
            Read(msg->SenderName) ?? "", Read(msg->ContentText) ?? ""));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnString(byte* s, nint user) => _received = Read((nint)s);

    private static int WithUtf8(string? s, Func<nint, int> f)
    {
        var p = s is null ? 0 : Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return f(p);
        }
        finally
        {
            if (p != 0)
                Marshal.FreeCoTaskMem(p);
        }
    }

    private sealed record ContextBuffer(long Skip, List<ContextMessage> Messages);
}

/// <summary>上下文中的一条消息。</summary>
internal sealed record ContextMessage(long Id, long Timestamp, bool IsOutgoing, string SenderName, string Text);

/// <summary>从回调参数复制的消息（回调返回后仍可使用）。</summary>
internal sealed record MessageInfo(long AccountId, long Id, long Timestamp, uint Flags, string SenderName, int ConvType,
    long StreamId, string StreamName, string Topic, long[] DmUserIds, string Text, long SenderId)
{
    public static unsafe MessageInfo From(ZqMessage* msg)
    {
        var ids = new long[Math.Max(msg->Conv.DmUserCount, 0)];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = ((long*)msg->Conv.DmUserIds)[i];
        return new MessageInfo(msg->AccountId, msg->MessageId, msg->Timestamp, msg->Flags, Host.Read(msg->SenderName) ?? "",
            msg->Conv.Type, msg->Conv.StreamId, Host.Read(msg->Conv.StreamName) ?? "", Host.Read(msg->Conv.Topic) ?? "", ids,
            Host.Read(msg->ContentText) ?? "", msg->SenderId);
    }

    public bool IsOutgoing => (Flags & Zq.MsgOutgoing) != 0;

    /// <summary>会话的说明，例如“频道“开发”的话题“发布””或“私聊”。</summary>
    public string Where => ConvType == Zq.ConvChannel ? $"频道“{StreamName}”的话题“{Topic}”" : "私聊";

    public static string FormatTime(long timestamp) =>
        DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// zq_on_panel 的参数：<c>{"action": "get" | "set" | "list" | "remove", "kind": "user" | "topic", "target", "key", "value"}</c>。
/// target 为 <c>{"account", "user", "name"}</c> 或 <c>{"account", "stream", "topic", "name"}</c>。
/// </summary>
internal sealed record PanelRequest(string Action, string Kind, long AccountId, long UserId, long StreamId, string Topic, string Name,
    string Key, JsonNode? Value)
{
    public static unsafe PanelRequest? From(byte* json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(Host.Read((nint)json) ?? "") as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (obj is null)
            return null;
        var target = obj["target"] as JsonObject ?? [];
        return new PanelRequest(JsonUtil.Str(obj, "action"), JsonUtil.Str(obj, "kind"), Long(target, "account"), Long(target, "user"),
            Long(target, "stream"), JsonUtil.Str(target, "topic"), JsonUtil.Str(target, "name"), JsonUtil.Str(obj, "key"), obj["value"]);
    }

    public bool IsUser => Kind == "user";

    private static long Long(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}

/// <summary>get_messages 使用的会话结构（字符串与 id 数组放在非托管内存中）。</summary>
internal sealed unsafe class NativeConversation : IDisposable
{
    private readonly ZqConversation* _conv;

    public NativeConversation(MessageInfo message)
    {
        _conv = (ZqConversation*)NativeMemory.AllocZeroed((nuint)sizeof(ZqConversation));
        _conv->Type = message.ConvType;
        if (message.ConvType == Zq.ConvChannel)
        {
            _conv->StreamId = message.StreamId;
            _conv->Topic = Marshal.StringToCoTaskMemUTF8(message.Topic);
        }
        else
        {
            _conv->DmUserCount = message.DmUserIds.Length;
            if (message.DmUserIds.Length > 0)
            {
                var ids = (long*)NativeMemory.Alloc((nuint)message.DmUserIds.Length, sizeof(long));
                for (var i = 0; i < message.DmUserIds.Length; i++)
                    ids[i] = message.DmUserIds[i];
                _conv->DmUserIds = (nint)ids;
            }
        }
    }

    public ZqConversation* Pointer => _conv;

    public void Dispose()
    {
        if (_conv->Topic != 0)
            Marshal.FreeCoTaskMem(_conv->Topic);
        if (_conv->DmUserIds != 0)
            NativeMemory.Free((void*)_conv->DmUserIds);
        NativeMemory.Free(_conv);
    }
}
