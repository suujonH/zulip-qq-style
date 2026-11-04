using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class QuoteBlocksTests
{
    private sealed class NullResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }

    private static IReadOnlyList<QuoteBlock> Find(string text) =>
        QuoteBlocks.Find(ZulipMarkdown.Parse(text, new NullResolver()), text);

    private const string Header = "@_**群主大大|8** [发言道](https://example.com/#narrow/dm/8,9-dm/near/603)：";

    [Fact]
    public void Find_QuoteInsertedByComposer()
    {
        var text = $"前面\n{Header}\n```quote\n第一行\n第二行\n```\n后面";
        var block = Assert.Single(Find(text));
        Assert.Equal("群主大大", block.SenderName);
        Assert.Equal(8, block.SenderId);
        Assert.Equal(603, block.MessageId);
        Assert.Equal(text.IndexOf("@_", StringComparison.Ordinal), block.Start);
        Assert.Equal(text.IndexOf("\n后面", StringComparison.Ordinal), block.End);
        Assert.Equal("第一行\n第二行", text[block.BodyStart..block.BodyEnd]);
        Assert.Equal((1, 5), (block.HeaderLine, block.CloseLine));
    }

    [Fact]
    public void Find_NestedFenceAndSeveralQuotes()
    {
        var nested = $"{Header}\n````quote\n```quote\n里面\n```\n外面\n````";
        var text = $"{nested}\n{Header.Replace("603", "604")}\n```quote\n\n```";
        var blocks = Find(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(nested.Length, blocks[0].End);
        Assert.Equal("```quote\n里面\n```\n外面", text[blocks[0].BodyStart..blocks[0].BodyEnd]);
        Assert.Equal(604, blocks[1].MessageId);
        Assert.Equal(text.Length, blocks[1].End);
    }

    [Fact]
    public void Find_IgnoresIncompleteOrOrdinaryQuotes()
    {
        // 围栏还没有结束
        Assert.Empty(Find($"{Header}\n```quote\n内容"));
        // 没有引用行的引用块
        Assert.Empty(Find("```quote\n内容\n```"));
        // 引用行后面不是引用围栏
        Assert.Empty(Find($"{Header}\n```\n代码\n```"));
        // 英文原文与半角冒号也认，没有 id 时 SenderId 为 null
        var english = Assert.Single(Find("@_**Bob** [said](https://example.com/#narrow/channel/1-a/topic/b/with/77):\n```quote\nhi\n```"));
        Assert.Null(english.SenderId);
        Assert.Equal(77, english.MessageId);
    }

    [Fact]
    public void HasOnlyQuotes()
    {
        var quote = $"{Header}\n```quote\n内容\n```";
        Assert.True(QuoteBlocks.HasOnlyQuotes($"{quote}\n"));
        Assert.True(QuoteBlocks.HasOnlyQuotes($"\n{quote}\n\n{quote}\n"));
        Assert.False(QuoteBlocks.HasOnlyQuotes($"{quote}\n回复"));
        Assert.False(QuoteBlocks.HasOnlyQuotes($"回复\n{quote}"));
        Assert.False(QuoteBlocks.HasOnlyQuotes("普通消息"));
        Assert.False(QuoteBlocks.HasOnlyQuotes(""));
    }
}
