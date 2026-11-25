using Bridge.DesktopCore;
using Xunit;

namespace Bridge.DesktopCore.Tests;

public sealed class RestrictedFileAccessTests
{
    private const string Root = @"C:\Users\Sam\Downloads";

    [Fact]
    public void Resolve_RelativePath_StaysUnderRoot()
    {
        var (path, error) = RestrictedFileAccess.Resolve(@"notes\report.txt", Root);
        Assert.Null(error);
        Assert.Equal(@"C:\Users\Sam\Downloads\notes\report.txt", path);
    }

    [Fact]
    public void Resolve_AbsolutePath_InsideRoot_Accepted()
    {
        var (path, error) = RestrictedFileAccess.Resolve(
            @"C:\Users\Sam\Downloads\a\b.txt", Root);
        Assert.Null(error);
        Assert.Equal(@"C:\Users\Sam\Downloads\a\b.txt", path);
    }

    [Fact]
    public void Resolve_AbsolutePath_OutsideRoot_Rejected()
    {
        var (_, error) = RestrictedFileAccess.Resolve(
            @"C:\Users\Sam\Documents\secret.txt", Root);
        Assert.NotNull(error);
        Assert.Equal("path_escape", error!.Code);
    }

    [Fact]
    public void Resolve_DotDotEscape_Rejected()
    {
        // Absolute escape through ..
        var (_, e1) = RestrictedFileAccess.Resolve(
            @"C:\Users\Sam\Downloads\..\..\Windows\secret.txt", Root);
        Assert.Equal("path_escape", e1!.Code);
        // Relative escape through leading ..
        var (_, e2) = RestrictedFileAccess.Resolve(@"..\..\secret.txt", Root);
        Assert.Equal("path_escape", e2!.Code);
        // .. that stays inside is fine.
        var (path, e3) = RestrictedFileAccess.Resolve(@"a\b\..\..\c.txt", Root);
        Assert.Null(e3);
        Assert.Equal(@"C:\Users\Sam\Downloads\c.txt", path);
    }

    [Fact]
    public void Resolve_RejectsUnc_Ads_DevicePaths()
    {
        Assert.Equal("invalid_path",
            RestrictedFileAccess.Resolve(@"\\server\share\file.txt", Root).Error!.Code);
        Assert.Equal("invalid_path",
            RestrictedFileAccess.Resolve(@"notes.txt:secret", Root).Error!.Code);
        Assert.Equal("invalid_path",
            RestrictedFileAccess.Resolve(@"\\?\C:\Users\Sam\Downloads\f.txt", Root).Error!.Code);
        Assert.Equal("invalid_path",
            RestrictedFileAccess.Resolve(@"\\.\C:\Users\Sam\Downloads\f.txt", Root).Error!.Code);
    }

    [Fact]
    public void Resolve_CaseInsensitiveRoot()
    {
        var (path, error) = RestrictedFileAccess.Resolve(
            @"c:\users\sam\downloads\F.TXT", Root);
        Assert.Null(error);
        Assert.NotNull(path);
    }

    [Fact]
    public void Resolve_ForwardSlashes_Normalized()
    {
        var (path, error) = RestrictedFileAccess.Resolve("a/b/c.txt", Root);
        Assert.Null(error);
        Assert.Equal(@"C:\Users\Sam\Downloads\a\b\c.txt", path);
    }

    [Fact]
    public void Resolve_NoRoot_Rejected()
    {
        var (_, error) = RestrictedFileAccess.Resolve("a.txt", "");
        Assert.Equal("no_authorized_root", error!.Code);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "bridge-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WriteReadRoundTrip_AndHash()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hello.txt");
        var content = System.Text.Encoding.UTF8.GetBytes("hello bridge");
        var (sha, error) = RestrictedFileAccess.Write(path, content, createOnly: false, expectedHash: null);
        Assert.Null(error);
        Assert.NotNull(sha);
        var (bytes, readSha, readError) = RestrictedFileAccess.Read(path);
        Assert.Null(readError);
        Assert.Equal(content, bytes);
        Assert.Equal(sha, readSha);
    }

    [Fact]
    public void Write_CreateOnly_RefusesExisting()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "x.txt");
        Assert.Null(RestrictedFileAccess.Write(path, [1], createOnly: true, expectedHash: null).Error);
        Assert.Equal("already_exists",
            RestrictedFileAccess.Write(path, [2], createOnly: true, expectedHash: null).Error!.Code);
    }

    [Fact]
    public void Write_ExpectedHash_MustMatch()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "x.txt");
        var (sha, _) = RestrictedFileAccess.Write(path, [1, 2, 3], createOnly: false, expectedHash: null);
        // Wrong hash refuses.
        Assert.Equal("hash_mismatch",
            RestrictedFileAccess.Write(path, [9], createOnly: false, expectedHash: new string('0', 64)).Error!.Code);
        // Right hash allows.
        Assert.Null(RestrictedFileAccess.Write(path, [9], createOnly: false, expectedHash: sha).Error);
        // Missing file + expected hash refuses.
        Assert.Equal("hash_mismatch",
            RestrictedFileAccess.Write(Path.Combine(dir, "nope.txt"), [1], createOnly: false, expectedHash: sha).Error!.Code);
    }

    [Fact]
    public void Write_LeavesNoTempFiles()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "x.txt");
        Assert.Null(RestrictedFileAccess.Write(path, [1, 2], createOnly: false, expectedHash: null).Error);
        Assert.Empty(Directory.EnumerateFiles(dir, ".bridge-*.tmp"));
        Assert.Single(Directory.EnumerateFiles(dir));
    }

    [Fact]
    public void Read_TooLarge_Refused()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "big.bin");
        File.WriteAllBytes(path, new byte[RestrictedFileAccess.MaxReadBytes + 1]);
        Assert.Equal("file_too_large", RestrictedFileAccess.Read(path).Error!.Code);
    }

    [Fact]
    public void List_BoundedAndTyped()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        var (entries, error) = RestrictedFileAccess.List(dir);
        Assert.Null(error);
        Assert.Equal(2, entries!.Count);
        Assert.Contains(entries, e => e.Name == "sub" && e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 1);
        Assert.Equal("not_found", RestrictedFileAccess.List(Path.Combine(dir, "nope")).Error!.Code);
    }

    [Fact]
    public void Move_WithinRoot_RenamesAndKeepsHash()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "a.txt");
        var dest = Path.Combine(dir, "sub", "b.txt");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(source, "move-me");
        var (sha, error) = RestrictedFileAccess.Move(source, dest);
        Assert.Null(error);
        Assert.NotNull(sha);
        Assert.False(File.Exists(source));
        Assert.Equal("move-me", File.ReadAllText(dest));
        var (_, readSha, _) = RestrictedFileAccess.Read(dest);
        Assert.Equal(sha, readSha);
    }

    [Fact]
    public void Move_DestinationExists_Refused_MissingSource_Refused()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "a.txt");
        var dest = Path.Combine(dir, "b.txt");
        File.WriteAllText(source, "x");
        File.WriteAllText(dest, "y");
        Assert.Equal("already_exists", RestrictedFileAccess.Move(source, dest).Error!.Code);
        Assert.Equal("not_found",
            RestrictedFileAccess.Move(Path.Combine(dir, "nope.txt"), Path.Combine(dir, "c.txt")).Error!.Code);
        Assert.Equal("not_found",
            RestrictedFileAccess.Move(source, Path.Combine(dir, "no-dir", "c.txt")).Error!.Code);
    }

    [Fact]
    public void Mkdir_Idempotent_And_RefusesFile()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "a", "b");
        Assert.Null(RestrictedFileAccess.Mkdir(target));
        Assert.True(Directory.Exists(target));
        Assert.Null(RestrictedFileAccess.Mkdir(target));
        var file = Path.Combine(dir, "f.txt");
        File.WriteAllText(file, "x");
        Assert.Equal("already_exists", RestrictedFileAccess.Mkdir(file)!.Code);
    }
}
