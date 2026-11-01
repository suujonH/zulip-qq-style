using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class FileKindTests
{
    [Theory]
    [InlineData("/user_uploads/2/ab/photo.PNG", FileKind.Image)]
    [InlineData("/user_uploads/2/ab/clip.mp4", FileKind.Video)]
    [InlineData("/user_uploads/2/ab/report.pdf", FileKind.Document)]
    [InlineData("/user_uploads/2/ab/notes.txt", FileKind.Document)]
    [InlineData("/user_uploads/2/ab/pack.7z", FileKind.Archive)]
    [InlineData("/user_uploads/2/ab/setup.exe", FileKind.Other)]
    [InlineData("/user_uploads/2/ab/noext", FileKind.Other)]
    public void Of(string path, FileKind expected) => Assert.Equal(expected, FileKinds.Of(path));

    [Theory]
    [InlineData("a.PDF", "Icon.FilePdf")]
    [InlineData("a.docx", "Icon.FileWord")]
    [InlineData("a.csv", "Icon.FileExcel")]
    [InlineData("a.pptx", "Icon.FilePowerpoint")]
    [InlineData("a.zip", "Icon.FileZip")]
    [InlineData("a.txt", "Icon.File")]
    [InlineData("a.png", "Icon.Image")]
    [InlineData("a.webm", "Icon.Video")]
    [InlineData("a.bin", "Icon.FileUnknown")]
    public void IconKey(string path, string expected) => Assert.Equal(expected, FileKinds.IconKey(path));
}
