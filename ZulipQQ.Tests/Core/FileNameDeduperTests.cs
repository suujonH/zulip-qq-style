using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class FileNameDeduperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqtest").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReturnsOriginal_WhenFree() =>
        Assert.Equal(Path.Combine(_dir, "资料.zip"), FileNameDeduper.NextFreePath(_dir, "资料.zip"));

    [Fact]
    public void AppendsCounter_WhenExists()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "a(1).txt"), "");
        Assert.Equal(Path.Combine(_dir, "a(2).txt"), FileNameDeduper.NextFreePath(_dir, "a.txt"));
    }

    [Fact]
    public void RespectsIsTaken()
    {
        var taken = new HashSet<string> { Path.Combine(_dir, "b.png") };
        Assert.Equal(Path.Combine(_dir, "b(1).png"), FileNameDeduper.NextFreePath(_dir, "b.png", taken.Contains));
    }

    [Fact]
    public void ReplacesInvalidChars() =>
        Assert.Equal(Path.Combine(_dir, "a_b_c.txt"), FileNameDeduper.NextFreePath(_dir, "a/b:c.txt"));

    [Fact]
    public void SkipsExistingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "c.txt"));
        Assert.Equal(Path.Combine(_dir, "c(1).txt"), FileNameDeduper.NextFreePath(_dir, "c.txt"));
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("aux.zip", "_aux.zip")]
    [InlineData("com1", "_com1")]
    [InlineData("console.txt", "console.txt")]
    public void EscapesReservedNames(string name, string expected) =>
        Assert.Equal(Path.Combine(_dir, expected), FileNameDeduper.NextFreePath(_dir, name));

    [Fact]
    public void NoExtension()
    {
        File.WriteAllText(Path.Combine(_dir, "README"), "");
        Assert.Equal(Path.Combine(_dir, "README(1)"), FileNameDeduper.NextFreePath(_dir, "README"));
    }

    [Theory]
    [InlineData("开发", "开发")]
    [InlineData(" .a:b. ", "a_b")]
    [InlineData("...", "_")]
    [InlineData("", "_")]
    [InlineData("localhost.localdomain:8443", "localhost.localdomain_8443")]
    [InlineData("NUL", "_NUL")]
    public void SanitizeDirectoryName(string input, string expected) =>
        Assert.Equal(expected, FileNameDeduper.SanitizeDirectoryName(input));

    [Fact]
    public void SanitizeDirectoryName_Truncates() =>
        Assert.Equal(60, FileNameDeduper.SanitizeDirectoryName(new string('x', 100)).Length);
}
