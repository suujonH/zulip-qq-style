using ZulipQQ.Core;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Tests.Core;

public class WidgetParserTests
{
    private const long Owner = 8;

    private static int _nextId;

    private static SubmessageDto Sub(long sender, string content) =>
        new() { Id = ++_nextId, MessageId = 100, SenderId = sender, MsgType = "widget", Content = content };

    [Fact]
    public void NoSubmessages_ReturnsNull() => Assert.Null(WidgetParser.Parse([], Owner));

    [Fact]
    public void UnknownWidget_ReturnsNull() =>
        Assert.Null(WidgetParser.Parse([Sub(Owner, """{"widget_type":"zform","extra_data":{}}""")], Owner));

    [Fact]
    public void BrokenDefinition_ReturnsNull() => Assert.Null(WidgetParser.Parse([Sub(Owner, "{")], Owner));

    [Fact]
    public void Poll_CountsVotesAndNewOptions()
    {
        var poll = Assert.IsType<PollWidget>(WidgetParser.Parse(
        [
            Sub(Owner, """{"widget_type":"poll","extra_data":{"question":"午饭","options":["面条","米饭"]}}"""),
            Sub(9, """{"type":"vote","key":"canned,1","vote":1}"""),
            Sub(Owner, """{"type":"vote","key":"canned,1","vote":1}"""),
            Sub(9, """{"type":"new_option","idx":1,"option":"饺子"}"""),
            Sub(10, """{"type":"vote","key":"9,1","vote":1}"""),
            // 撤销投票
            Sub(Owner, """{"type":"vote","key":"canned,1","vote":-1}"""),
            // 不存在的选项、格式错误的操作都忽略
            Sub(10, """{"type":"vote","key":"canned,9","vote":1}"""),
            Sub(10, "not json"),
        ], Owner));

        Assert.Equal("午饭", poll.Question);
        Assert.Equal(["面条", "米饭", "饺子"], poll.Options.Select(o => o.Text));
        Assert.Empty(poll.Options[0].Voters);
        Assert.Equal([9L], poll.Options[1].Voters);
        Assert.Equal([10L], poll.Options[2].Voters);
    }

    [Fact]
    public void Poll_RepeatedVoteCountsOnce_DuplicateOptionIgnored()
    {
        var poll = Assert.IsType<PollWidget>(WidgetParser.Parse(
        [
            Sub(Owner, """{"widget_type":"poll","extra_data":{"question":"Q","options":["A"]}}"""),
            Sub(9, """{"type":"vote","key":"canned,0","vote":1}"""),
            Sub(9, """{"type":"vote","key":"canned,0","vote":1}"""),
            Sub(9, """{"type":"new_option","idx":1,"option":"A"}"""),
        ], Owner));

        Assert.Single(poll.Options);
        Assert.Equal([9L], poll.Options[0].Voters);
    }

    [Fact]
    public void Poll_OnlySenderCanChangeQuestion()
    {
        var poll = Assert.IsType<PollWidget>(WidgetParser.Parse(
        [
            Sub(Owner, """{"widget_type":"poll","extra_data":{"question":"旧","options":[]}}"""),
            Sub(9, """{"type":"question","question":"别人改的"}"""),
            Sub(Owner, """{"type":"question","question":"新"}"""),
        ], Owner));

        Assert.Equal("新", poll.Question);
    }

    [Fact]
    public void Poll_AppliesInIdOrder()
    {
        var definition = Sub(Owner, """{"widget_type":"poll","extra_data":{"question":"Q","options":["A"]}}""");
        var vote = Sub(9, """{"type":"vote","key":"canned,0","vote":1}""");
        var unvote = Sub(9, """{"type":"vote","key":"canned,0","vote":-1}""");

        var poll = Assert.IsType<PollWidget>(WidgetParser.Parse([unvote, vote, definition], Owner));

        Assert.Empty(poll.Options[0].Voters);
    }

    [Fact]
    public void Todo_TasksAndStrike()
    {
        var todo = Assert.IsType<TodoWidget>(WidgetParser.Parse(
        [
            Sub(Owner, """{"widget_type":"todo","extra_data":{"task_list_title":"发布","tasks":[{"task":"打包","desc":"zip"},{"task":"测试","desc":""}]}}"""),
            Sub(9, """{"type":"new_task","key":2,"task":"公告","desc":"写说明","completed":false}"""),
            Sub(9, """{"type":"strike","key":"0,canned"}"""),
            Sub(Owner, """{"type":"strike","key":"2,9"}"""),
            Sub(Owner, """{"type":"strike","key":"2,9"}"""),
            Sub(9, """{"type":"new_task_list_title","title":"别人改的"}"""),
        ], Owner));

        Assert.Equal("发布", todo.Title);
        Assert.Equal(["打包", "测试", "公告"], todo.Tasks.Select(t => t.Text));
        Assert.Equal("zip", todo.Tasks[0].Description);
        Assert.Equal([true, false, false], todo.Tasks.Select(t => t.Completed));
    }

    [Fact]
    public void Todo_SenderChangesTitle()
    {
        var todo = Assert.IsType<TodoWidget>(WidgetParser.Parse(
        [
            Sub(Owner, """{"widget_type":"todo","extra_data":{"task_list_title":"","tasks":[]}}"""),
            Sub(Owner, """{"type":"new_task_list_title","title":"本周"}"""),
        ], Owner));

        Assert.Equal("本周", todo.Title);
        Assert.Empty(todo.Tasks);
    }
}
