using Gpp;
using Xunit;

namespace Gpp.Tests;

// The engine names the demo, so GPP has to recognise the file it just wrote among the ones that were
// already there. Getting this wrong renames somebody else's demo, which is why it is tested.
public class DemoFilesTests
{
    private static DateTime At(int minute) => new(2026, 11, 7, 0, minute, 0, DateTimeKind.Utc);

    private static Func<string, DateTime> Clock(Dictionary<string, DateTime> times) => path => times[path];

    [Fact]
    public void PicksTheOneFileThatIsNew()
    {
        var times = new Dictionary<string, DateTime> { ["old.dem"] = At(1), ["new.dem"] = At(2) };
        var picked = DemoFiles.PickNew(["old.dem"], ["old.dem", "new.dem"], Clock(times));
        Assert.Equal("new.dem", picked);
    }

    [Fact]
    public void PicksTheNewestWhenSeveralAreNew()
    {
        var times = new Dictionary<string, DateTime>
        {
            ["old.dem"] = At(1),
            ["a.dem"] = At(2),
            ["b.dem"] = At(5),
            ["c.dem"] = At(3),
        };
        var picked = DemoFiles.PickNew(["old.dem"], ["old.dem", "a.dem", "b.dem", "c.dem"], Clock(times));
        Assert.Equal("b.dem", picked);
    }

    // A recording that captured nothing writes no file at all, and that must not look like a success.
    [Fact]
    public void NothingNewIsNull()
    {
        var times = new Dictionary<string, DateTime> { ["old.dem"] = At(1) };
        Assert.Null(DemoFiles.PickNew(["old.dem"], ["old.dem"], Clock(times)));
    }

    [Fact]
    public void AnEmptyDirectoryBeforeIsFine()
    {
        var times = new Dictionary<string, DateTime> { ["only.dem"] = At(1) };
        Assert.Equal("only.dem", DemoFiles.PickNew([], ["only.dem"], Clock(times)));
    }

    // A file that was there before is never picked, however recently it was touched: an old demo being
    // rewritten by something else is not this match's.
    [Fact]
    public void AKnownFileIsNeverPickedEvenIfNewer()
    {
        var times = new Dictionary<string, DateTime> { ["old.dem"] = At(9), ["new.dem"] = At(2) };
        var picked = DemoFiles.PickNew(["old.dem"], ["old.dem", "new.dem"], Clock(times));
        Assert.Equal("new.dem", picked);
    }

    [Fact]
    public void NothingAtAllIsNull()
    {
        Assert.Null(DemoFiles.PickNew([], [], _ => At(0)));
    }

    [Fact]
    public void TheEngineDirectoryIsWhereAutorecordWrites()
    {
        Assert.Equal("replays", DemoFiles.EngineDirectory);
    }
}
