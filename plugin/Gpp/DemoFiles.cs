namespace Gpp;

// Which file the engine just wrote, and what to call it instead. No engine in here on purpose - the
// picking is the part that can go wrong quietly, so it is the part that is tested.
//
// GPP does not name the demo it records. On this build only a recording that starts at a level load
// captures anything (see Demos.cs), and that recording is started by the engine's own tv_autorecord,
// which picks the name: "auto-<date>-<time>-<map>-<hostname>.dem", in csgo/replays. So the name is
// applied afterwards, by moving the file the engine wrote.
// [SOURCE: measured on this server 2026-11-07]
internal static class DemoFiles
{
    // Where tv_autorecord writes, relative to csgo/. Not a cvar: there is no tv_ cvar for the directory
    // in this build's cvarlist, so the engine's choice is hard-coded here.
    internal const string EngineDirectory = "replays";

    // The newest demo in `now` that was not in `before`. Null when the engine wrote nothing - which is
    // what a recording that never captured a frame does, so a null here is a real failure, not a race.
    internal static string? PickNew(IEnumerable<string> before, IEnumerable<string> now,
                                    Func<string, DateTime> writtenAt)
    {
        var known = new HashSet<string>(before, StringComparer.Ordinal);
        string? newest = null;
        DateTime newestAt = DateTime.MinValue;

        foreach (var candidate in now)
        {
            if (known.Contains(candidate)) continue;
            var at = writtenAt(candidate);
            if (newest != null && at <= newestAt) continue;
            newest = candidate;
            newestAt = at;
        }

        return newest;
    }
}
