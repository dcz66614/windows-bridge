using Bridge.DesktopCore;
using Xunit;

namespace Bridge.DesktopCore.Tests;

public sealed class CoordinateMapperTests
{
    [Fact]
    public void ImageToNative_ScalesWithFloor()
    {
        // 800x600 image of a 1920x1080 crop at (100, 50).
        var (x, y) = CoordinateMapper.ImageToNative(400, 300, 800, 600, 100, 50, 1920, 1080);
        Assert.Equal(100 + 960, x);
        Assert.Equal(50 + 540, y);
    }

    [Fact]
    public void ImageToNative_FloorsFractionalPixels()
    {
        // 3px image of a 10px crop: image 1 -> native 3 (floor(10/3)=3).
        var (x, _) = CoordinateMapper.ImageToNative(1, 0, 3, 1, 0, 0, 10, 1);
        Assert.Equal(3, x);
    }

    [Fact]
    public void ImageToNative_RejectsOutsidePixels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoordinateMapper.ImageToNative(800, 0, 800, 600, 0, 0, 1920, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoordinateMapper.ImageToNative(-1, 0, 800, 600, 0, 0, 1920, 1080));
    }

    [Fact]
    public void NativeToSendInput_HandlesNegativeVirtualOrigin()
    {
        // Virtual desktop from (-1920, 0), 3840 wide: the primary's
        // left edge (native 0) maps to 1920*65535/3839 = 32776, and
        // the vertical middle (540 of 1080) to 32798.
        var (ax, ay) = CoordinateMapper.NativeToSendInput(0, 540, -1920, 0, 3840, 1080);
        Assert.Equal(32776, ax);
        Assert.Equal(32798, ay);
    }

    [Fact]
    public void NativeToSendInput_RejectsOutsideVirtualDesktop()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoordinateMapper.NativeToSendInput(3840, 0, 0, 0, 3840, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoordinateMapper.NativeToSendInput(0, -1, 0, 0, 1920, 1080));
    }

    [Fact]
    public void NativeToSendInput_EdgesMapToZeroAndMax()
    {
        var (ax0, _) = CoordinateMapper.NativeToSendInput(0, 0, 0, 0, 1920, 1080);
        var (ax1, ay1) = CoordinateMapper.NativeToSendInput(1919, 1079, 0, 0, 1920, 1080);
        Assert.Equal(0, ax0);
        Assert.Equal(65535, ax1);
        Assert.Equal(65535, ay1);
    }
}

public sealed class InputValidationTests
{
    [Theory]
    [InlineData("CTRL", "C")]
    [InlineData("WIN", "L")]
    [InlineData("F12")]
    [InlineData("CTRL", "ALT", "DELETE")]
    public void ValidateKeys_AcceptsAllowlist(params string[] keys) =>
        Assert.Null(InputValidation.ValidateKeys(keys));

    [Theory]
    [InlineData("CMD")]
    [InlineData("ctrl")]
    [InlineData("F13")]
    [InlineData("SUPER")]
    public void ValidateKeys_RejectsOutsideAllowlist(string key) =>
        Assert.NotNull(InputValidation.ValidateKeys([key]));

    [Fact]
    public void ValidateKeys_RejectsRepeatsAndTooMany()
    {
        Assert.NotNull(InputValidation.ValidateKeys(["CTRL", "CTRL"]));
        Assert.NotNull(InputValidation.ValidateKeys(["A", "B", "C", "D", "E"]));
        Assert.NotNull(InputValidation.ValidateKeys([]));
    }

    [Fact]
    public void ValidateTypeText_Bounds()
    {
        Assert.Null(InputValidation.ValidateTypeText("你好，世界 🌍"));
        Assert.NotNull(InputValidation.ValidateTypeText(""));
        Assert.NotNull(InputValidation.ValidateTypeText(new string('x', 2001)));
        Assert.NotNull(InputValidation.ValidateTypeText("a\0b"));
    }

    [Fact]
    public void ValidateTimes_ClockSkewAndTtl()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(InputValidation.ValidateTimes(now.AddSeconds(30), now.AddSeconds(45), now));
        Assert.NotNull(InputValidation.ValidateTimes(now, now.AddSeconds(45), now));
        Assert.NotNull(InputValidation.ValidateTimes(now.AddSeconds(-20), now.AddSeconds(-5), now));
        Assert.Null(InputValidation.ValidateTimes(now, now.AddSeconds(15), now));
    }

    [Fact]
    public void ValidateArtifact_Rules()
    {
        var sha = new string('a', 64);
        Assert.Null(InputValidation.ValidateArtifact(sha, "image/png", 1024));
        Assert.NotNull(InputValidation.ValidateArtifact(sha, "image/jpeg", 1024));
        Assert.NotNull(InputValidation.ValidateArtifact(sha, "image/png", 0));
        Assert.NotNull(InputValidation.ValidateArtifact(sha, "image/png", 4 * 1024 * 1024 + 1));
        Assert.NotNull(InputValidation.ValidateArtifact(new string('A', 64), "image/png", 1024));
        Assert.NotNull(InputValidation.ValidateArtifact("abc", "image/png", 1024));
    }
}

public sealed class CommandJournalTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "desktopcore-journal-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void Accept_Execute_Finish_AndReplayReturnsReceipt()
    {
        var db = TempDb();
        using var journal = new CommandJournal(db);
        var hash = CommandJournal.BindingHash("cmd-1", "dev", "lease", 1, 1,
            "input.click", "{\"x\":10,\"y\":20}", "owner", "ctrl");
        var (accepted, _) = journal.Accept("cmd-1", "dev", "lease", 1, 1,
            "input.click", hash, "owner", "ctrl");
        Assert.True(accepted);
        journal.MarkExecuting("cmd-1");
        journal.Finish("cmd-1", "succeeded", "{\"ok\":true}");

        var (accepted2, replay) = journal.Accept("cmd-1", "dev", "lease", 1, 1,
            "input.click", hash, "owner", "ctrl");
        Assert.False(accepted2);
        Assert.NotNull(replay);
        Assert.Equal("succeeded", replay!.Status);
        Assert.True(replay.Replay);
    }

    [Fact]
    public void Accept_SameIdDifferentBinding_Conflicts()
    {
        var db = TempDb();
        using var journal = new CommandJournal(db);
        var hash1 = CommandJournal.BindingHash("cmd-1", "dev", "lease", 1, 1,
            "input.click", "{\"x\":10}", "owner", "ctrl");
        var hash2 = CommandJournal.BindingHash("cmd-1", "dev", "lease", 1, 1,
            "input.click", "{\"x\":99}", "owner", "ctrl");
        journal.Accept("cmd-1", "dev", "lease", 1, 1, "input.click", hash1, "owner", "ctrl");
        Assert.Throws<IdempotencyConflictException>(() =>
            journal.Accept("cmd-1", "dev", "lease", 1, 1, "input.click", hash2, "owner", "ctrl"));
    }

    [Fact]
    public void Reopen_MarksInterruptedUncertain()
    {
        var db = TempDb();
        using (var journal = new CommandJournal(db))
        {
            var hash = CommandJournal.BindingHash("cmd-9", "dev", "lease", 1, 1,
                "input.click", "{}", "owner", "ctrl");
            journal.Accept("cmd-9", "dev", "lease", 1, 1, "input.click", hash, "owner", "ctrl");
            journal.MarkExecuting("cmd-9");
            // Crash: dispose without finishing.
        }
        using var reopened = new CommandJournal(db);
        var receipt = reopened.Lookup("cmd-9");
        Assert.NotNull(receipt);
        Assert.Equal("uncertain", receipt!.Status);
    }
}

public sealed class ObservationConstraintsTests
{
    [Fact]
    public void IsFresh_ThirtySecondWindow()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(ObservationConstraints.IsFresh(now.AddSeconds(-29), now));
        Assert.False(ObservationConstraints.IsFresh(now.AddSeconds(-31), now));
        Assert.False(ObservationConstraints.IsFresh(now.AddSeconds(30), now));
    }

    [Fact]
    public void Generations_MustAllMatch()
    {
        var a = new ObservationConstraints.Generations(1, 2, 3);
        Assert.True(ObservationConstraints.GenerationsMatch(a, new(1, 2, 3)));
        Assert.False(ObservationConstraints.GenerationsMatch(a, new(2, 2, 3)));
        Assert.False(ObservationConstraints.GenerationsMatch(a, new(1, 3, 3)));
        Assert.False(ObservationConstraints.GenerationsMatch(a, new(1, 2, 4)));
    }

    [Fact]
    public void GenerationTracker_Increments()
    {
        var tracker = new GenerationTracker();
        tracker.NoteLayoutChanged();
        tracker.NoteForegroundChanged();
        tracker.NoteInputExecuted();
        Assert.Equal(new ObservationConstraints.Generations(1, 1, 1), tracker.Current);
    }
}
