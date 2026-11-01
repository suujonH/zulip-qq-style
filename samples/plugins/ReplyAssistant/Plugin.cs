using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ModelCenterApi;
using PluginShared;

namespace ReplyAssistant;

/// <summary>
/// 回复助手（ZulipQQ 内置插件，参考 jev-chat-jarvis 的“先读懂，再回复”）：消息右键菜单“分析这条消息”→
/// 在工作线程上先用分析模型（jev，OpenRouter decisions 接口）按场景分析对方的消息，
/// 再把此前的聊天、这条消息与分析结论发给写回复模型，在消息下方显示分析、意图、风险、对方需要、建议，
/// 以及候选回复按钮（点击填入输入框，由用户自己发送）。两个模型都可以单独使用。
/// 场景按话题、身份按好友在话题资料面板 / 用户资料卡（zq_on_panel）中设置，与分析结果一起保存在插件自己的数据库中（见 <see cref="Store"/>），
/// 消息再次显示时重新附加分析结果。
/// </summary>
internal static unsafe class Plugin
{
    private const int MenuAnalyze = 1;
    private const int MaxReplies = 3;
    private const string SceneField = "scene";
    private const string IdentityField = "identity";

    [UnmanagedCallersOnly(EntryPoint = "zq_plugin_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(ZqHostApi* api) => Host.Init(api, "回复助手") ? Zq.Ok : Zq.ErrInvalidArg;

    [UnmanagedCallersOnly(EntryPoint = "zq_on_message_menu", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMessageMenu(int menuId, ZqMessage* msg)
    {
        if (menuId != MenuAnalyze)
            return Zq.Ok;
        var message = MessageInfo.From(msg);
        var settings = Host.GetSettings();
        var analysisModel = ModelCenterClient.Selection(settings, "analysis_model");
        var model = ModelCenterClient.Selection(settings, "model");
        if (analysisModel is null && model is null)
        {
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "请先在“设置 > 插件 > 回复助手 > 设置”中选择分析模型或写回复模型" });
            return Zq.Ok;
        }
        var request = new Request(message, settings, analysisModel, model, JsonUtil.Str(settings, "prompt"),
            JsonUtil.Str(settings, "background").Trim(),
            int.TryParse(JsonUtil.Str(settings, "context"), out var n) ? Math.Clamp(n, 0, 50) : 10);
        Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "正在分析…" });
        if (!Host.Post(() => Analyze(request)))
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = "待分析的消息过多，请稍后再试" });
        return Zq.Ok;
    }

    private sealed record Request(MessageInfo Message, JsonObject Settings, JsonObject? AnalysisModel, JsonObject? Model, string Prompt,
        string Background, int ContextCount);

    // 消息显示时重新附加保存的分析结果（回调所属的账号即消息所属的账号）
    [UnmanagedCallersOnly(EntryPoint = "zq_on_messages_shown", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMessagesShown(byte* json)
    {
        if (JsonNode.Parse(Host.Read((nint)json) ?? "{}") is not JsonObject obj || obj["account"]?.GetValue<long>() is not { } accountId)
            return Zq.Ok;
        var ids = (obj["messages"] as JsonArray ?? []).Select(n => n?.GetValue<long>() ?? 0).Where(id => id > 0).ToList();
        foreach (var (messageId, content) in Store.Analyses(accountId, ids))
        {
            if (JsonNode.Parse(content) is JsonObject annotation)
                Host.SetAnnotation(messageId, annotation);
        }
        return Zq.Ok;
    }

    // 话题资料面板（场景）与用户资料卡（身份），以及插件设置中的列表
    [UnmanagedCallersOnly(EntryPoint = "zq_on_panel", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnPanel(byte* json)
    {
        if (PanelRequest.From(json) is not { } request)
            return Zq.ErrInvalidArg;
        var settings = Host.GetSettings();
        switch (request.Action)
        {
            case "list":
                Host.ReplyPanel(new JsonObject { ["entries"] = Entries(request.IsUser, settings) });
                return Zq.Ok;
            case "remove":
                if (request.IsUser)
                    Store.SetIdentity(request.AccountId, request.UserId, "", "");
                else
                    Store.SetTopicScene(request.AccountId, request.StreamId, request.Topic, "", "");
                return Zq.Ok;
            case "set":
                var value = request.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                if (request.IsUser && request.Key == IdentityField && (value.Length == 0 || Scenes.ParseIdentity(value) is not null))
                    Store.SetIdentity(request.AccountId, request.UserId, request.Name, value);
                else if (!request.IsUser && request.Key == SceneField && (value.Length == 0 || JevCatalog.Instance.Scene(value) is not null))
                    Store.SetTopicScene(request.AccountId, request.StreamId, request.Topic, request.Name, value);
                break;
            case "get":
                break;
            default:
                return Zq.ErrInvalidArg;
        }
        var field = request.IsUser
            ? new JsonObject
            {
                ["key"] = IdentityField, ["label"] = "身份", ["type"] = "select", ["options"] = Scenes.IdentityOptions(),
                ["value"] = Store.GetIdentity(request.AccountId, request.UserId) ?? "",
            }
            : new JsonObject
            {
                ["key"] = SceneField, ["label"] = "分析场景", ["type"] = "select", ["options"] = Scenes.SceneOptions(settings),
                ["value"] = Store.GetTopicScene(request.AccountId, request.StreamId, request.Topic) ?? "",
            };
        Host.ReplyPanel(new JsonObject { ["fields"] = new JsonArray(field) });
        return Zq.Ok;
    }

    private static JsonArray Entries(bool users, JsonObject settings)
    {
        if (users)
        {
            return [.. Store.Identities().Select(i => (JsonNode)new JsonObject
            {
                ["target"] = new JsonObject { ["account"] = i.AccountId, ["user"] = i.UserId, ["name"] = i.Name },
                ["text"] = Scenes.ParseIdentity(i.Identity)?.Label ?? "",
            })];
        }
        return [.. Store.TopicScenes().Select(t => (JsonNode)new JsonObject
        {
            ["target"] = new JsonObject { ["account"] = t.AccountId, ["stream"] = t.StreamId, ["topic"] = t.Topic, ["name"] = t.Name },
            ["text"] = JevCatalog.Instance.Scene(t.Scene)?.Label ?? "",
        })];
    }

    // 工作线程
    private static void Analyze(Request request)
    {
        var message = request.Message;
        if (!Host.SelectAccount(message.AccountId))
            return;
        var context = Host.GetContextMessages(message, request.ContextCount);

        var lines = new List<string>();
        JevResult? jev = null;
        SceneChoice? scene = null;
        if (request.AnalysisModel is { } analysisModel)
        {
            scene = Scenes.Resolve(message, request.Settings);
            jev = JevAnalysis.Analyze(analysisModel, scene, message, context);
            if (jev.Error is not null)
                Host.Log(Zq.LogError, $"jev 分析消息 {message.Id} 失败：{jev.Error}");
            lines.Add($"分析场景：{scene.Label}（可在话题资料面板、用户资料卡中修改）");
            lines.AddRange(jev.Lines());
            if (request.Model is null)
            {
                Show(message, new JsonObject { ["text"] = string.Join("\n", lines) }, save: jev.Error is null);
                return;
            }
            Host.SetAnnotation(message.Id, new JsonObject { ["text"] = string.Join("\n", lines.Append("正在生成候选回复…")) });
        }

        var user = $"会话：{message.Where}\n";
        if (request.Background.Length > 0)
            user += $"关于我：{request.Background}\n";
        if (context.Count > 0)
        {
            user += "此前的聊天：\n" + string.Join("\n", context.Select(m =>
                $"[{MessageInfo.FormatTime(m.Timestamp)}] {(m.IsOutgoing ? $"我（{m.SenderName}）" : m.SenderName)}：{m.Text}")) + "\n";
        }
        if (jev?.Situation() is { } situation)
            user += $"分析模型（jev）对这条消息的结论（关系：{scene!.Label}）：{situation}\n";
        var sender = message.IsOutgoing ? $"我（{message.SenderName}）" : message.SenderName;
        user += $"需要分析的消息（[{MessageInfo.FormatTime(message.Timestamp)}] {sender}）：\n{message.Text}";

        var reply = ModelCenterClient.Ask(request.Model!, request.Prompt, user, out var error);
        if (reply is null)
        {
            Host.Log(Zq.LogError, $"分析消息 {message.Id} 失败：{error}");
            lines.Add($"生成回复失败：{error}");
            Show(message, new JsonObject { ["text"] = string.Join("\n", lines) }, save: false);
            return;
        }
        Show(message, ToAnnotation(reply, lines), save: jev?.Error is null);
    }

    // save：保存结果，消息再次显示时重新附加（失败的结果不保存）
    private static void Show(MessageInfo message, JsonObject annotation, bool save)
    {
        if (save)
            Store.SetAnalysis(message.AccountId, message.Id, annotation);
        var code = Host.SetAnnotation(message.Id, annotation);
        if (code != Zq.Ok)
            Host.Log(Zq.LogWarning, $"显示分析结果失败：{code}");
        else
            Host.Log(Zq.LogInfo, $"已分析消息 {message.Id}");
    }

    // 模型返回 JSON 时整理为几行说明与候选回复按钮；不是 JSON 时整段显示。prefix 为 jev 分析的几行
    private static JsonObject ToAnnotation(string reply, IReadOnlyList<string> prefix)
    {
        if (JsonUtil.ExtractJson(reply) is not { } json)
            return new JsonObject { ["text"] = string.Join("\n", prefix.Append(reply.Trim())) };
        var lines = new[] { ("意图", "intent"), ("风险", "risk"), ("对方需要", "needs"), ("建议", "advice") }
            .Select(f => (f.Item1, Value: JsonUtil.Str(json, f.Item2).Trim()))
            .Where(f => f.Value.Length > 0)
            .Select(f => $"{f.Item1}：{f.Value}");
        var replies = (json["replies"] as JsonArray ?? [])
            .Select(r => r is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
            .Where(s => s.Length > 0)
            .Take(MaxReplies)
            .Select((s, i) => (JsonNode)new JsonObject { ["label"] = $"填入回复 {i + 1}", ["fill"] = s });
        return new JsonObject { ["text"] = string.Join("\n", prefix.Concat(lines)), ["actions"] = new JsonArray([.. replies]) };
    }
}
