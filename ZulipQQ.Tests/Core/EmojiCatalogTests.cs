using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class EmojiCatalogTests
{
    private static readonly IReadOnlyList<EmojiEntry> Unicode = EmojiCatalog.ParseUnicode(Fixture.Text("emoji_data_sample.json"));

    private static readonly EmojiEntry Parrot = new("parrot", "7", EmojiEntry.RealmType, [], null, "/user_avatars/2/emoji/images/7.gif");

    [Fact]
    public void ParseUnicode_FirstNameIsCanonical()
    {
        var thumbs = Assert.Single(Unicode, e => e.Code == "1f44d");
        Assert.Equal("+1", thumbs.Name);
        Assert.Equal(["thumbs_up", "like"], thumbs.Aliases);
        Assert.Equal("👍", thumbs.Text);
        Assert.Equal(EmojiEntry.UnicodeType, thumbs.Type);
    }

    [Fact]
    public void ParseUnicode_MultiCodepointAndKeycap()
    {
        Assert.Equal("🧑‍💻", Unicode.Single(e => e.Name == "technologist").Text);
        Assert.Equal("#⃣", Unicode.Single(e => e.Name == "hash").Text);
    }

    [Fact]
    public void ParseUnicode_FacesFirst_KeycapsLast()
    {
        Assert.Equal("1f600", Unicode[0].Code);
        Assert.Equal("hash", Unicode[^1].Name);
    }

    [Fact]
    public void ParseUnicode_SkipsInvalidCode() => Assert.DoesNotContain(Unicode, e => e.Name == "broken");

    [Fact]
    public void TryGet_ByAlias_ReturnsCanonicalEntry()
    {
        var catalog = new EmojiCatalog([], Unicode);
        Assert.Equal("folded_hands", catalog.TryGet("pray")?.Name);
        Assert.Null(catalog.TryGet("nope"));
    }

    [Fact]
    public void RealmEmoji_ComesFirst_AndOverridesSameName()
    {
        var smile = Parrot with { Name = "smile", Code = "8" };
        var catalog = new EmojiCatalog([Parrot, smile], Unicode);
        Assert.Equal(Parrot, catalog.All[0]);
        Assert.Equal(EmojiEntry.RealmType, catalog.TryGet("smile")?.Type);
    }

    [Fact]
    public void Search_PrefixBeforeContains()
    {
        var catalog = new EmojiCatalog([], Unicode);
        // grinning（别名 happy）、hash 以 ha 开头，按目录顺序在前；folded_hands 只是包含 ha
        Assert.Equal(["grinning", "hash", "folded_hands"], catalog.Search(":ha").Select(e => e.Name));
        Assert.Equal(["heart"], catalog.Search("love").Select(e => e.Name));
        Assert.Equal(["joy"], catalog.Search("ears").Select(e => e.Name));
        Assert.Equal(catalog.All.Count, catalog.Search("  ").Count);
    }
}
