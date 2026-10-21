using ZulipQQ.Core;
using ZulipQQ.Zulip;

namespace ZulipQQ.Tests.Zulip;

public class NarrowAndFormTests
{
    [Fact]
    public void Narrow_Topic_Serializes()
    {
        var json = ZulipJson.Serialize(Narrow.For(new TopicKey(4, "构建失败"), 8));
        Assert.Equal(
            """[{"operator":"channel","operand":4,"negated":false},{"operator":"topic","operand":"构建失败","negated":false}]""",
            json);
    }

    [Fact]
    public void Narrow_Topic_UsesOriginalName()
    {
        var parsed = (TopicKey)ConversationKey.Parse("t:4:hello world");
        var json = ZulipJson.Serialize(Narrow.For(parsed, 8, "Hello World"));
        Assert.Contains("\"Hello World\"", json);
    }

    [Fact]
    public void Narrow_Dm()
    {
        Assert.Equal("""[{"operator":"dm","operand":[9,10],"negated":false}]""",
            ZulipJson.Serialize(Narrow.For(DmKey.Create([10, 9], 8), 8)));
        Assert.Equal("""[{"operator":"dm","operand":[8],"negated":false}]""",
            ZulipJson.Serialize(Narrow.For(DmKey.Create([], 8), 8)));
    }

    [Fact]
    public void Narrow_Negated()
    {
        Assert.Equal("""[{"operator":"has","operand":"image","negated":true},{"operator":"search","operand":"a b","negated":false}]""",
            ZulipJson.Serialize(new[] { Narrow.Has("image", negated: true), Narrow.Search("a b") }));
    }

    [Fact]
    public void FormBuilder_EncodesValues()
    {
        var form = new FormBuilder()
            .Add("topic", "开发 & 测试")
            .Add("num", 42)
            .Add("flag", true)
            .AddJson("to", new long[] { 1, 2 });
        Assert.Equal("topic=%E5%BC%80%E5%8F%91%20%26%20%E6%B5%8B%E8%AF%95&num=42&flag=true&to=%5B1%2C2%5D", form.ToQueryString());
        Assert.Equal(4, form.Pairs.Count());
    }
}
