using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using ModelCenterApi;
using PluginShared;

namespace Translator;

/// <summary>
/// 翻译（ZulipQQ 内置插件）：消息右键菜单“翻译”→ 先在消息下方显示“正在翻译…”，在工作线程上通过模型中心等插件提供的模型翻译，
/// 成功后用 replace 临时替换气泡的显示内容（宿主提供“显示原文”切换，不修改消息原文）。
/// 译文保存在插件自己的数据库中（数据目录中的 translator.db），消息再次显示时重新附加；再次点击“翻译”重新翻译。
/// </summary>
internal static unsafe class Plugin
{
    private const int MenuTranslate = 1;
    private const string ModelField = "model";
    private const string TestButton = "test";
    private const string TestText = "你好";
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS translation(
            account INTEGER NOT NULL, message_id INTEGER NOT NULL, content TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY(account, message_id));
        """;

    private static readonly Lock DbGate = new();
    private static SqliteDb? _db;
    private static bool _dbOpened;

    [UnmanagedCallersOnly(EntryPoint = "zq_plugin_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(ZqHostApi* api) => Host.Init(api, "翻译") ? Zq.Ok : Zq.ErrInvalidArg;

    [UnmanagedCallersOnly(EntryPoint = "zq_on_message_menu", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMessageMenu(int menuId, ZqMessage* msg)
    {
        if (menuId != MenuTranslate)
            return Zq.Ok;
        var message = MessageInfo.From(msg);
        if (message.Text.Trim().Length == 0)
        {
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "这条消息没有可翻译的文字" });
            return Zq.Ok;
        }
        var settings = Host.GetSettings();
        if (ModelCenterClient.Selection(settings, ModelField) is not { } model)
        {
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "请先在“设置 > 插件 > 翻译 > 设置”中选择模型" });
            return Zq.Ok;
        }
        var (language, prompt) = Prompt(settings);
        // 回调中调用宿主函数使用回调所属的账号
        Host.SetAnnotation(message.Id, new JsonObject { ["text"] = $"正在翻译为{language}…" });
        if (!Host.Post(() => Translate(message, model, prompt)))
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "待翻译的消息过多，请稍后再试" });
        return Zq.Ok;
    }

    // 消息显示时重新附加保存的译文（回调所属的账号即消息所属的账号）
    [UnmanagedCallersOnly(EntryPoint = "zq_on_messages_shown", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMessagesShown(byte* json)
    {
        if (JsonNode.Parse(Host.Read((nint)json) ?? "{}") is not JsonObject obj || obj["account"]?.GetValue<long>() is not { } accountId)
            return Zq.Ok;
        var ids = (obj["messages"] as JsonArray ?? []).Select(n => n?.GetValue<long>() ?? 0).Where(id => id > 0).ToList();
        foreach (var (messageId, translated) in Saved(accountId, ids))
            Host.SetAnnotation(messageId, new JsonObject { ["replace"] = translated });
        return Zq.Ok;
    }

    // 设置中的“测试”：values 为表单当前的值（尚未保存）
    [UnmanagedCallersOnly(EntryPoint = "zq_on_settings_button", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnSettingsButton(byte* json)
    {
        var obj = JsonNode.Parse(Host.Read((nint)json) ?? "{}") as JsonObject ?? new JsonObject();
        if (JsonUtil.Str(obj, "key") != TestButton)
            return Zq.Ok;
        var settings = obj["values"] as JsonObject ?? new JsonObject();
        if (ModelCenterClient.Selection(settings, ModelField) is not { } model)
        {
            Host.SetSettingsText(TestButton, null, "请先选择模型");
            return Zq.Ok;
        }
        var (language, prompt) = Prompt(settings);
        Host.SetSettingsText(TestButton, null, $"正在把“{TestText}”翻译为{language}…");
        if (!Host.Post(() => Test(model, prompt)))
            Host.SetSettingsText(TestButton, null, "待翻译的消息过多，请稍后再试");
        return Zq.Ok;
    }

    // 目标语言与替换后的提示词
    private static (string Language, string Prompt) Prompt(JsonObject settings)
    {
        var language = JsonUtil.Str(settings, "language").Trim() is { Length: > 0 } l ? l : "简体中文";
        return (language, JsonUtil.Str(settings, "prompt").Replace("{language}", language));
    }

    // 工作线程
    private static void Test(JsonObject model, string prompt)
    {
        var reply = ModelCenterClient.Ask(model, prompt, TestText, out var error);
        Host.SetSettingsText(TestButton, null, reply?.Trim() is { Length: > 0 } translated
            ? $"成功：{translated}"
            : $"失败：{error ?? "模型没有返回文字"}");
    }

    // 工作线程
    private static void Translate(MessageInfo message, JsonObject model, string prompt)
    {
        if (!Host.SelectAccount(message.AccountId))
            return;
        var reply = ModelCenterClient.Ask(model, prompt, message.Text, out var error);
        if (error is not null)
            Host.Log(Zq.LogError, $"翻译消息 {message.Id} 失败：{error}");
        var translated = reply?.Trim() ?? "";
        if (translated.Length > 0)
            Save(message.AccountId, message.Id, translated);
        var annotation = translated.Length > 0
            ? new JsonObject { ["replace"] = translated }
            : new JsonObject { ["text"] = $"翻译失败：{error ?? "模型没有返回文字"}" };
        var code = Host.SetAnnotation(message.Id, annotation);
        if (code != Zq.Ok)
            Host.Log(Zq.LogWarning, $"显示译文失败：{code}");
        else if (reply is not null)
            Host.Log(Zq.LogInfo, $"已翻译消息 {message.Id}");
    }

    // 保存的译文：(消息 id, 译文)
    private static List<(long MessageId, string Text)> Saved(long accountId, List<long> messageIds)
    {
        if (messageIds.Count == 0)
            return [];
        try
        {
            return Db()?.Query($"SELECT message_id, content FROM translation WHERE account = ? AND message_id IN ({string.Join(",", messageIds)});",
                r => (r.Long(0), r.Text(1)), accountId) ?? [];
        }
        catch (InvalidOperationException ex)
        {
            Host.Log(Zq.LogWarning, $"读取译文失败：{ex.Message}");
            return [];
        }
    }

    private static void Save(long accountId, long messageId, string translated)
    {
        try
        {
            Db()?.Execute("""
                INSERT INTO translation(account, message_id, content, updated_at) VALUES(?, ?, ?, ?)
                ON CONFLICT(account, message_id) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at;
                """, accountId, messageId, translated, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (InvalidOperationException ex)
        {
            Host.Log(Zq.LogWarning, $"保存译文失败：{ex.Message}");
        }
    }

    private static SqliteDb? Db()
    {
        lock (DbGate)
        {
            if (!_dbOpened)
            {
                _dbOpened = true;
                if (Host.GetDataDir() is { Length: > 0 } dir)
                    _db = SqliteDb.Open(Path.Combine(dir, "translator.db"), Schema);
            }
            return _db;
        }
    }
}
