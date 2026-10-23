using System.Text.Json;
using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

/// <summary>样本来自测试服务器的 <c>POST /messages/render</c>。</summary>
public class ZulipHtmlTests
{
    private static readonly JsonElement Samples = Fixture.Json("rendered_samples.json");

    private static string Sample(string name) => Samples.GetProperty(name).GetString()!;

    private static string RealImage => Samples.GetProperty("real_image")[0].GetString()!;

    [Fact]
    public void Preview_Quote_ShowsOnlyNewContent() =>
        Assert.Equal("阿杰 说: 回复内容", ZulipHtml.ToPreview(Sample("quote")));

    [Fact]
    public void Preview_Code() => Assert.Equal("[代码]", ZulipHtml.ToPreview(Sample("code")));

    [Fact]
    public void Preview_Emoji_ToUnicode() => Assert.Equal("你好 😄 👍", ZulipHtml.ToPreview(Sample("emoji")));

    [Fact]
    public void Preview_List_JoinsWithSpaces() => Assert.Equal("一 二 甲 乙", ZulipHtml.ToPreview(Sample("list")));

    [Fact]
    public void Preview_Time_IsLocal()
    {
        var expected = new DateTimeOffset(2026, 10, 22, 2, 0, 0, TimeSpan.Zero).ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, ZulipHtml.ToPreview(Sample("time")));
    }

    [Fact]
    public void Preview_Math() => Assert.Equal("[公式] 与 [公式] [公式]", ZulipHtml.ToPreview(Sample("math")));

    [Fact]
    public void Preview_Spoiler() => Assert.Equal("[剧透]", ZulipHtml.ToPreview(Sample("spoiler")));

    [Fact]
    public void Preview_Mentions() => Assert.Equal("@all @topic @Members", ZulipHtml.ToPreview(Sample("mentions")));

    [Fact]
    public void Preview_ImageLinkWithoutInlinePreview() => Assert.Equal("[图片]", ZulipHtml.ToPreview(Sample("img")));

    [Fact]
    public void Preview_ImageWithInlinePreview_ShowsOnce() => Assert.Equal("截图 [图片]", ZulipHtml.ToPreview(RealImage));

    [Fact]
    public void Preview_File() =>
        Assert.Equal("给你发个压缩包 [文件]资料.zip",
            ZulipHtml.ToPreview("<p>给你发个压缩包 <a href=\"/user_uploads/2/85/w2m/资料.zip\">资料.zip</a></p>"));

    [Fact]
    public void Preview_Formatting() => Assert.Equal("粗 斜 删 code 链接", ZulipHtml.ToPreview(Sample("strong")));

    [Fact]
    public void Preview_Empty() => Assert.Equal("", ZulipHtml.ToPreview(""));

    [Fact]
    public void PlainText_Quote_KeepsQuoteMarks() =>
        Assert.Equal("阿杰 说:\n> 原文内容\n回复内容", ZulipHtml.ToPlainText(Sample("quote")));

    [Fact]
    public void PlainText_Code_KeepsSource() => Assert.Equal("print(1)", ZulipHtml.ToPlainText(Sample("code")));

    [Fact]
    public void PlainText_List() => Assert.Equal("• 一\n• 二\n1. 甲\n2. 乙", ZulipHtml.ToPlainText(Sample("list")));

    [Fact]
    public void PlainText_ImageKeepsFileName() => Assert.Equal("截图 截图-开发.png", ZulipHtml.ToPlainText(RealImage));

    [Fact]
    public void PlainText_ExternalLinkKeepsUrl() =>
        Assert.Equal("粗 斜 删 code 链接 (https://example.com)", ZulipHtml.ToPlainText(Sample("strong")));

    [Fact]
    public void PlainText_LineBreaks() =>
        Assert.Equal("a\nb\nc", ZulipHtml.ToPlainText("<p>a<br>\nb</p>\n<p>c</p>"));

    [Fact]
    public void ExtractUploads_DedupesAndDetectsImages()
    {
        var uploads = ZulipHtml.ExtractUploads(RealImage);
        var upload = Assert.Single(uploads);
        Assert.Equal("/user_uploads/2/ac/LxWueOIW7MbTvYUwx5Oq51ne/截图-开发.png", upload.Path);
        Assert.Equal("截图-开发.png", upload.FileName);
        Assert.True(upload.IsImage);
        Assert.False(ZulipHtml.HasFileAttachment(RealImage));
    }

    [Fact]
    public void ExtractUploads_RewrittenOwnRealmUrlAndUrlText()
    {
        // 粘贴本组织的完整地址时，服务器渲染为不带开头斜杠的相对链接，链接文字保留完整地址
        const string html = "<p><a href=\"user_uploads/2/ab/x/%E8%B5%84%E6%96%99.zip\">https://zulip.example.com/user_uploads/2/ab/x/%E8%B5%84%E6%96%99.zip</a></p>";
        var upload = Assert.Single(ZulipHtml.ExtractUploads(html));
        Assert.Equal("/user_uploads/2/ab/x/%E8%B5%84%E6%96%99.zip", upload.Path);
        Assert.Equal("资料.zip", upload.FileName);
        Assert.False(upload.IsImage);
        Assert.True(ZulipHtml.HasFileAttachment(html));
    }

    [Theory]
    [InlineData("报告", "report.pdf")]
    [InlineData("季度报告.PDF", "季度报告.PDF")]
    [InlineData("", "report.pdf")]
    public void UploadLinkOf_KeepsExtension(string text, string expected) =>
        Assert.Equal(expected, ZulipHtml.UploadLinkOf("/user_uploads/2/id/report.pdf", text).FileName);

    [Fact]
    public void ExtractUploads_OtherServer_IsIgnored() => Assert.Empty(ZulipHtml.ExtractUploads(
        "<p><a href=\"https://other.example/user_uploads/2/ab/x/report.pdf\">report.pdf</a></p>"));

    [Theory]
    [InlineData("emoji emoji-1f604", "😄")]
    [InlineData("emoji emoji-1f469-200d-1f4bb", "👩‍💻")]
    [InlineData("emoji emoji-zz", null)]
    [InlineData("emoji", null)]
    public void EmojiFromClass(string cls, string? expected) => Assert.Equal(expected, ZulipHtml.EmojiFromClass(cls));

    [Theory]
    [InlineData("/user_uploads/thumbnail/2/ac/x.png/840x560.webp", null)]
    [InlineData("#narrow/near/1", null)]
    [InlineData("https://example.com/user_uploads/2/a/b.txt", null)]
    [InlineData("/user_uploads/2/a/b.txt?x=1", "/user_uploads/2/a/b.txt")]
    [InlineData("user_uploads/2/a/b.txt", "/user_uploads/2/a/b.txt")]
    public void NormalizeUploadPath(string href, string? expected) =>
        Assert.Equal(expected, ZulipHtml.NormalizeUploadPath(href));

    private const string UploadedImage =
        "<p><a href=\"/user_uploads/2/ac/x/a.png\">a.png</a></p>\n" +
        "<div class=\"message_inline_image\"><a href=\"/user_uploads/2/ac/x/a.png\" title=\"a.png\"><img data-original-dimensions=\"320x200\" src=\"/user_uploads/thumbnail/2/ac/x/a.png/840x560.webp\"></a></div>";

    [Fact]
    public void AttachmentOnly_UploadedImage() => Assert.True(ZulipHtml.IsAttachmentOnly(UploadedImage));

    [Fact]
    public void AttachmentOnly_FileLinks() => Assert.True(ZulipHtml.IsAttachmentOnly(Sample("img")));

    [Fact]
    public void AttachmentOnly_TextWithImage_IsFalse() => Assert.False(ZulipHtml.IsAttachmentOnly(RealImage));

    [Fact]
    public void AttachmentOnly_PlainText_IsFalse() => Assert.False(ZulipHtml.IsAttachmentOnly(Sample("strong")));

    [Fact]
    public void AttachmentOnly_ExternalPreview_IsFalse() => Assert.False(ZulipHtml.IsAttachmentOnly(
        "<div class=\"message_inline_image\"><a href=\"https://example.com/a.png\"><img src=\"/external_content/abc\"></a></div>"));

    [Fact]
    public void ExtractInlineImages_UploadedAndExternal()
    {
        const string html = UploadedImage +
            "<div class=\"message_inline_image\"><a href=\"https://example.com/b.png\"><img src=\"/external_content/abc\"></a></div>" +
            "<div class=\"message_inline_image message_inline_video\"><a href=\"/user_uploads/2/ac/x/v.mp4\"><video src=\"/user_uploads/2/ac/x/v.mp4\"></video></a></div>";
        var images = ZulipHtml.ExtractInlineImages(html);
        Assert.Equal(2, images.Count);
        Assert.Equal("/user_uploads/thumbnail/2/ac/x/a.png/840x560.webp", images[0].Src);
        Assert.Equal("/user_uploads/2/ac/x/a.png", images[0].Original);
        Assert.Equal("/external_content/abc", images[1].Original);
    }

    [Fact]
    public void ExtractInlineImages_PlaceholderUsesUploadPath()
    {
        const string html = "<div class=\"message_inline_image\"><a href=\"/user_uploads/2/ac/x/a.png\"><img class=\"image-loading-placeholder\" src=\"/static/images/loading/loader-black.svg\"></a></div>";
        Assert.Equal("/user_uploads/2/ac/x/a.png", Assert.Single(ZulipHtml.ExtractInlineImages(html)).Src);
    }

    [Fact]
    public void ExtractInlineImages_SpanPreview()
    {
        const string html = "<div class=\"message-thumbnail-gallery\"><span class=\"message_inline_image\"><a href=\"/user_uploads/2/ac/x/a.png\"><img src=\"/user_uploads/thumbnail/2/ac/x/a.png/840x560.webp\"></a></span></div>";
        Assert.Equal("/user_uploads/thumbnail/2/ac/x/a.png/840x560.webp", Assert.Single(ZulipHtml.ExtractInlineImages(html)).Src);
    }
}