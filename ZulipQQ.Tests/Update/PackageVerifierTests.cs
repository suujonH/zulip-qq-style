using System.IO.Compression;
using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

public class PackageVerifierTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Manifest_parses_valid_json_and_round_trips()
    {
        var manifest = new UpdateManifest("1.2.3", "ZulipQQ-v1.2.3-win-x64.zip", Hash, 10);
        Assert.Equal(manifest, UpdateManifest.Parse(manifest.ToJson()));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":"0.01","package":"a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}""")]
    [InlineData("""{"version":"1.0.0","package":"../evil.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}""")]
    [InlineData("""{"version":"1.0.0","package":"a.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}""")]
    [InlineData("""{"version":"1.0.0","package":"a.zip","sha256":"abc","size":1}""")]
    [InlineData("""{"version":"1.0.0","package":"a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":0}""")]
    public void Manifest_rejects_invalid_content(string json)
    {
        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void Archive_with_matching_hash_passes_and_any_change_fails()
    {
        using var temp = new TempDir();
        var (zip, manifest) = PackageBuilder.Zip("1.0.0", PackageBuilder.Version("new"), temp.Path);
        var path = temp.Sub("p.zip");
        File.WriteAllBytes(path, zip);
        PackageVerifier.VerifyArchive(path, manifest);

        zip[^1] ^= 0xFF;
        File.WriteAllBytes(path, zip);
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyArchive(path, manifest));

        File.WriteAllBytes(path, zip[..^1]);
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyArchive(path, manifest));
    }

    [Fact]
    public void Extract_rejects_entries_outside_target()
    {
        using var temp = new TempDir();
        var zipPath = temp.Sub("evil.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
            writer.Write("x");
        }

        Assert.Throws<PackageInvalidException>(() => PackageVerifier.ExtractSafely(zipPath, temp.Sub("out")));
        Assert.False(File.Exists(temp.Sub("outside.txt")));
    }

    [Fact]
    public void Extract_rejects_non_zip_content()
    {
        using var temp = new TempDir();
        var zipPath = temp.Sub("bad.zip");
        File.WriteAllText(zipPath, "this is not a zip");
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.ExtractSafely(zipPath, temp.Sub("out")));
    }

    [Fact]
    public void Tree_requires_list_app_and_updater()
    {
        using var temp = new TempDir();
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyTree(temp.Path));

        PackageBuilder.WriteTree(temp.Path, new Dictionary<string, string> { [UpdateLayout.UpdaterExeName] = "x" });
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyTree(temp.Path));
    }

    [Fact]
    public void Tree_detects_missing_and_modified_files()
    {
        using var temp = new TempDir();
        PackageBuilder.WriteTree(temp.Path, PackageBuilder.Version("new", @"plugins\a\a.dll"));
        Assert.Equal(3, PackageVerifier.VerifyTree(temp.Path).Count);

        File.WriteAllText(temp.Sub(@"plugins\a\a.dll"), "tampered");
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyTree(temp.Path));

        File.Delete(temp.Sub(@"plugins\a\a.dll"));
        Assert.Throws<PackageInvalidException>(() => PackageVerifier.VerifyTree(temp.Path));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData(@"..\evil.dll")]
    [InlineData(@"a\..\..\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"C:evil.dll")]
    [InlineData(@"\evil.dll")]
    [InlineData(@".update-backup\x.dll")]
    public void File_list_rejects_unsafe_paths(string path)
    {
        Assert.Throws<PackageInvalidException>(() => FileEntry.ParseList([$"{Hash}  {path}"]));
    }

    [Theory]
    [InlineData("short  a.dll")]
    [InlineData("a.dll")]
    public void File_list_rejects_malformed_lines(string line)
    {
        Assert.Throws<PackageInvalidException>(() => FileEntry.ParseList([line]));
    }

    [Fact]
    public void File_list_normalizes_forward_slashes_and_skips_blank_lines()
    {
        var entries = FileEntry.ParseList(["", $"{Hash}  plugins/a/a.dll", "  "]);
        Assert.Equal(@"plugins\a\a.dll", Assert.Single(entries).Path);
    }
}
