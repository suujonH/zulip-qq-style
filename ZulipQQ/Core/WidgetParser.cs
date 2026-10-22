using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Core;

/// <summary>消息中的小部件（投票、待办）。</summary>
public abstract record Widget;

/// <summary>投票的一个选项与投票人。</summary>
public sealed record PollOption(string Text, IReadOnlyList<long> Voters);

public sealed record PollWidget(string Question, IReadOnlyList<PollOption> Options) : Widget;

public sealed record TodoTask(string Text, string Description, bool Completed);

public sealed record TodoWidget(string Title, IReadOnlyList<TodoTask> Tasks) : Widget;

/// <summary>
/// 按 Zulip 网页端的小部件协议汇总子消息。第一条子消息是定义：
/// <c>{"widget_type":"poll","extra_data":{"question","options"}}</c> 或
/// <c>{"widget_type":"todo","extra_data":{"task_list_title","tasks":[{"task","desc"}]}}</c>；
/// 之后按 id 顺序应用每次操作：
/// <list type="bullet">
/// <item>投票：<c>new_option</c>（键为 <c>发送者,idx</c>，预置选项为 <c>canned,序号</c>）、
/// <c>vote</c>（1 投票、-1 撤销）、<c>question</c>（只有消息发送者可以修改）。</item>
/// <item>待办：<c>new_task</c>（键为 <c>key,发送者</c>，预置任务为 <c>序号,canned</c>）、
/// <c>strike</c>（切换完成状态）、<c>new_task_list_title</c>（只有消息发送者可以修改）。</item>
/// </list>
/// 同名的选项 / 任务只保留第一个。格式不认识时返回 null，按普通消息显示。
/// </summary>
public static class WidgetParser
{
    public static Widget? Parse(IReadOnlyList<SubmessageDto> submessages, long messageSenderId)
    {
        var ordered = submessages.Where(s => s.MsgType == "widget").OrderBy(s => s.Id).ToList();
        if (ordered.Count == 0)
            return null;
        try
        {
            using var definition = JsonDocument.Parse(ordered[0].Content);
            var root = definition.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var extra = root.TryGetProperty("extra_data", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            return String(root, "widget_type") switch
            {
                "poll" => ParsePoll(extra, ordered.Skip(1), messageSenderId),
                "todo" => ParseTodo(extra, ordered.Skip(1), messageSenderId),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class MutableOption(string text)
    {
        public string Text { get; } = text;
        public List<long> Voters { get; } = [];
    }

    private static PollWidget ParsePoll(JsonElement extra, IEnumerable<SubmessageDto> events, long messageSenderId)
    {
        var question = String(extra, "question");
        var options = new List<(string Key, MutableOption Option)>();

        void AddOption(string key, string text)
        {
            if (text.Length > 0 && !options.Exists(o => o.Key == key || o.Option.Text == text))
                options.Add((key, new MutableOption(text)));
        }

        if (extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("options", out var canned) && canned.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var o in canned.EnumerateArray())
                AddOption($"canned,{i++}", o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "");
        }

        foreach (var s in events)
        {
            if (Event(s) is not { } ev)
                continue;
            switch (String(ev, "type"))
            {
                case "new_option" when Integer(ev, "idx") is { } idx:
                    AddOption($"{s.SenderId},{idx}", String(ev, "option"));
                    break;
                case "vote" when Integer(ev, "vote") is { } vote:
                    var key = String(ev, "key");
                    if (options.Find(o => o.Key == key).Option is not { } option)
                        break;
                    option.Voters.Remove(s.SenderId);
                    if (vote == 1)
                        option.Voters.Add(s.SenderId);
                    break;
                case "question" when s.SenderId == messageSenderId:
                    question = String(ev, "question");
                    break;
            }
        }
        return new PollWidget(question, options.Select(o => new PollOption(o.Option.Text, o.Option.Voters.ToList())).ToList());
    }

    private static TodoWidget ParseTodo(JsonElement extra, IEnumerable<SubmessageDto> events, long messageSenderId)
    {
        var title = String(extra, "task_list_title");
        var tasks = new List<(string Key, TodoTask Task)>();

        void AddTask(string key, string text, string desc, bool completed)
        {
            if (text.Length > 0 && !tasks.Exists(t => t.Key == key || t.Task.Text == text))
                tasks.Add((key, new TodoTask(text, desc, completed)));
        }

        if (extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("tasks", out var canned) && canned.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var t in canned.EnumerateArray())
                AddTask($"{i++},canned", String(t, "task"), String(t, "desc"), false);
        }

        foreach (var s in events)
        {
            if (Event(s) is not { } ev)
                continue;
            switch (String(ev, "type"))
            {
                case "new_task" when Integer(ev, "key") is { } key:
                    AddTask($"{key},{s.SenderId}", String(ev, "task"), String(ev, "desc"),
                        ev.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True);
                    break;
                case "strike":
                    var strikeKey = String(ev, "key");
                    var index = tasks.FindIndex(t => t.Key == strikeKey);
                    if (index >= 0)
                        tasks[index] = (strikeKey, tasks[index].Task with { Completed = !tasks[index].Task.Completed });
                    break;
                case "new_task_list_title" when s.SenderId == messageSenderId:
                    title = String(ev, "title");
                    break;
            }
        }
        return new TodoWidget(title, tasks.Select(t => t.Task).ToList());
    }

    // 一次操作的 JSON；格式错误的操作跳过（不影响其它操作）
    private static JsonElement? Event(SubmessageDto s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s.Content);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 整数字段；缺失、不是数字或不是整数时为 null（不抛异常）
    private static long? Integer(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
