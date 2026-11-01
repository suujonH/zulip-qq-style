using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelCenterApi;
using PluginShared;

namespace ReplyAssistant;

/// <summary>
/// jev 分析的结论（按 jev 的阈值解读后）：对方在做的事、信号、语气（各自最多 3 项，概率从高到低）、
/// 不确定的题数、没有答案的题数、冲突程度。<see cref="Error"/> 不为 null 时分析失败。
/// </summary>
internal sealed record JevResult(IReadOnlyList<string> Doing, IReadOnlyList<string> Cues, IReadOnlyList<string> Tones, int Unsure,
    int Missing, string? Friction, string? Error)
{
    public static JevResult Failed(string error) => new([], [], [], 0, 0, null, error);

    /// <summary>显示在消息下方的几行。</summary>
    public IEnumerable<string> Lines()
    {
        if (Error is not null)
        {
            yield return $"jev 分析失败：{Error}";
            yield break;
        }
        yield return Doing.Count > 0 ? $"对方在：{string.Join("、", Doing)}" : "对方在：没有明确识别到";
        if (Cues.Count > 0)
            yield return $"信号：{string.Join("、", Cues)}";
        if (Tones.Count > 0)
            yield return $"语气：{string.Join("、", Tones)}";
        if (Friction is not null)
            yield return $"冲突：{Friction}";
        if (Missing > 0)
            yield return $"有 {Missing} 项没能读出";
    }

    /// <summary>交给写回复模型的分析结论（文字，不含概率）；失败时为 null。</summary>
    public string? Situation() => Error is not null ? null : string.Join("；", Lines());
}

/// <summary>
/// 按 jev 的方式分析对方最新的几条消息：并行发送两个 decisions 请求（行为与语气、冲突程度），按 jev 的阈值解读。
/// 对应 jev 的 AnalysisEngine（state、frictionState、behaviourQuestions、frictionQuestions 与 Thresholds）。
/// </summary>
internal static class JevAnalysis
{
    // jev Thresholds：≥ HIT 为有，≤ MISS 为没有，之间为不确定
    private const double Hit = 0.7;
    private const double Miss = 0.3;
    // 冲突请求只带最新一轮之前的这几条消息
    private const int Recent = 4;
    private const int MaxLabels = 3;
    private const string FrictionOtherId = "friction.other";
    private const string FrictionSelfId = "friction.self";

    /// <summary>
    /// 工作线程调用（阻塞到两个请求都返回）。<paramref name="context"/> 为这条消息之前的消息（按时间先后）。
    /// 这条消息是自己发送的时不分析（jev 只分析对方的消息）。
    /// </summary>
    public static JevResult Analyze(JsonObject model, SceneChoice choice, MessageInfo message, IReadOnlyList<ContextMessage> context)
    {
        if (message.IsOutgoing)
            return JevResult.Failed("这条是你发送的消息，jev 只分析对方的消息");
        var catalog = JevCatalog.Instance;
        var all = context.Append(new ContextMessage(message.Id, message.Timestamp, false, message.SenderName, message.Text)).ToList();
        // 最新一轮：这条消息与它之前同一个人连续发送的消息
        var start = all.Count - 1;
        while (start > 0 && !all[start - 1].IsOutgoing && all[start - 1].SenderName == message.SenderName)
            start--;
        // 群聊中 jev 仍只区分“我 / 对方”，在文字前加上发言人
        var group = message.ConvType == Zq.ConvChannel || message.DmUserIds.Length > 1;
        JsonNode ToState(ContextMessage m) => new JsonObject
        {
            ["from"] = m.IsOutgoing ? "user" : "other",
            ["text"] = group && !m.IsOutgoing ? $"{m.SenderName}: {m.Text}" : m.Text,
        };
        var earlier = all.Take(start).ToList();
        var latest = new JsonArray([.. all.Skip(start).Select(ToState)]);
        var state = new JsonObject
        {
            ["relationship_note"] = choice.Note,
            ["earlier_messages"] = new JsonArray([.. earlier.Select(ToState)]),
            ["latest_messages"] = latest.DeepClone(),
        };
        var frictionState = new JsonObject
        {
            ["relationship_note"] = choice.Note,
            ["earlier_messages"] = new JsonArray([.. earlier.TakeLast(Recent).Select(ToState)]),
            ["latest_messages"] = latest.DeepClone(),
        };

        var questions = choice.Scene.Questions
            .Where(q => q.Relationships is null || (choice.Relationship is { } r && q.Relationships.Contains(r.Id)))
            .ToList();
        var behaviourQuestions = new JsonObject();
        foreach (var q in questions)
        {
            behaviourQuestions[q.Id] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = $"{q.Question} {catalog.DataNote}",
                ["criteria"] = new JsonObject { ["true"] = q.Yes, ["false"] = q.No },
            };
        }
        var frictionQuestions = new JsonObject
        {
            [FrictionOtherId] = Score(catalog, choice.Scene, catalog.FrictionOtherQuestion),
            [FrictionSelfId] = Score(catalog, choice.Scene, catalog.FrictionSelfQuestion),
        };

        string? behaviourError = null, frictionError = null;
        var behaviourTask = Task.Run(() => ModelCenterClient.Decide(model, state, behaviourQuestions, out behaviourError));
        var frictionTask = Task.Run(() => ModelCenterClient.Decide(model, frictionState, frictionQuestions, out frictionError));
        Task.WaitAll(behaviourTask, frictionTask);
        var answers = behaviourTask.Result;
        if (answers is null)
            return JevResult.Failed(behaviourError ?? "没有答案");
        if (frictionTask.Result is null)
            Host.Log(Zq.LogWarning, $"jev 冲突程度分析失败：{frictionError}");

        var probabilities = new Dictionary<string, double>();
        foreach (var q in questions)
        {
            if (answers[q.Id] is JsonObject a && JsonUtil.Str(a, "type") == "noul" && a["noul"] is JsonValue v
                && v.TryGetValue<double>(out var p) && p is >= 0 and <= 1)
                probabilities[q.Id] = p;
        }
        List<string> Labels(Func<JevQuestion, bool> filter) => questions.Where(q => filter(q) && probabilities.GetValueOrDefault(q.Id) >= Hit)
            .OrderByDescending(q => probabilities[q.Id]).Take(MaxLabels).Select(q => q.Label).ToList();

        var behaviours = questions.Where(q => q.Kind == "behavior" && q.Role != "cue").ToList();
        return new JevResult(
            Labels(q => q.Kind == "behavior" && q.Role != "cue"),
            Labels(q => q.Kind == "behavior" && q.Role == "cue"),
            Labels(q => q.Kind == "tone"),
            behaviours.Count(q => probabilities.TryGetValue(q.Id, out var p) && p is > Miss and < Hit),
            questions.Count(q => !probabilities.ContainsKey(q.Id)),
            Friction(catalog, frictionTask.Result),
            null);
    }

    // 冲突程度题：6 个等级，例子为空时不写 examples
    private static JsonObject Score(JevCatalog catalog, JevScene scene, string question) => new()
    {
        ["type"] = "score",
        ["instructions"] = $"{question} {catalog.DataNote}",
        ["criteria"] = new JsonArray([.. catalog.FrictionLevels.Select((level, i) =>
        {
            var examples = i < scene.FrictionExamples.Count ? scene.FrictionExamples[i] : [];
            var criterion = new JsonObject { ["what"] = level.What };
            if (examples.Count > 0)
                criterion["examples"] = new JsonArray([.. examples.Select(e => (JsonNode)JsonValue.Create(e))]);
            return (JsonNode)criterion;
        })]),
    };

    // 两方中较高的一方（0～5），≥ 1 时显示等级与来源；没有答案时为 null
    private static string? Friction(JevCatalog catalog, JsonObject? answers)
    {
        static double? ScoreOf(JsonObject? answers, string id) =>
            answers?[id] is JsonObject a && a["score"] is JsonValue v && v.TryGetValue<double>(out var s) ? s : null;

        var other = ScoreOf(answers, FrictionOtherId);
        var self = ScoreOf(answers, FrictionSelfId);
        if (other is null && self is null)
            return null;
        var level = Math.Max(other ?? 0, self ?? 0);
        if (level < 1)
            return null;
        var source = (other >= 1, self >= 1) switch
        {
            (true, true) => "双方都有",
            (true, false) => "来自对方",
            _ => "来自你",
        };
        var index = Math.Clamp((int)Math.Round(level), 0, catalog.FrictionLevels.Count - 1);
        return $"{catalog.FrictionLevels[index].Label}（{source}）";
    }
}
