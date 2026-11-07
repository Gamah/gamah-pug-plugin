using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;

namespace Gpp;

// A GOTV demo per pug. The shape of this is dictated by one engine fact, measured rather than chosen:
//
//   **A recording only captures frames if it starts at a level load.** Started at any other moment it
//   runs, reports no error, and finalises as a ~110 KB header with "Recording time -0.0" in it. The
//   engine says why itself: "CHLTVServerAsync::ExecuteDelayedCall StartAutoRecordingCall_t with start
//   tick == 0". That is true of tv_record at any path, delay or tv_record_immediate setting, and of
//   tv_autorecord toggled on mid-map. Every demo this server ever produced by tv_record on go-live was
//   one of those headers.
//
// So the plugin does not name or start the recording. The engine's tv_autorecord does both, at the level
// load, and GPP only decides *which* level loads record:
//
//   - a pug's map change arms it (tv_autorecord 1) just before the changelevel;
//   - the load that follows starts the recording, and OnMapStart disarms it again, so an idle reload, a
//     .map practice change or a CS2 update restart records nothing;
//   - the end of the match stops it, which is what keeps a demo from running through the hours of warmup
//     that follow a pug;
//   - and then the file the engine wrote is moved to demo_path under GPP's own demo_name_format, which is
//     what the stats site lists and links.
//
// The cost of the level-load rule is that the demo starts with the draft and the knife round rather than
// at LIVE. The alternative was reloading the map at go-live - a loading screen for ten people at the
// worst possible moment - and that was weighed and rejected.
//
// Two further measured details worth keeping:
//   - the file does not exist until the recording stops, and the write is asynchronous
//     ("HLTVServerAsync"), so the move waits for a new file whose size has settled.
//   - tv_stoprecord does finalise an autorecord recording, with its frames. It does *not* stop one
//     started by tv_record, which is another symptom of the same broken path.
// [SOURCE: measured on this server 2026-11-07, CS2 PatchVersion 1.41.9.6/14196. Engine behaviour, not a
// documented contract - re-check it after a CS2 update.]
public partial class Gpp
{
    private bool _recording;
    private string _demoFile = "";

    // Armed between a pug's map change and the level load that starts the recording.
    private bool _demoArmed;

    // The demos already in the engine's directory when the recording started. What is there afterwards
    // and not in here is the file it just wrote.
    private string[] _demosBefore = [];

    private CounterStrikeSharp.API.Modules.Timers.Timer? _demoCollectTimer;

    private string EngineDemoDirectory =>
        Path.Join(Server.GameDirectory, "csgo", DemoFiles.EngineDirectory).Replace('\\', '/');

    private string[] EngineDemos()
    {
        try
        {
            return Directory.Exists(EngineDemoDirectory)
                ? Directory.GetFiles(EngineDemoDirectory, "*.dem")
                : [];
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Could not list {Directory}: {Message}", EngineDemoDirectory, exception.Message);
            return [];
        }
    }

    // Called just before a pug's changelevel. Everything that matters happens at the load that follows.
    private void ArmDemoForMapChange()
    {
        _demoArmed = false;
        if (!Config.Match.DemosEnabled) return;

        if (!(ConVar.Find("tv_enable")?.GetPrimitiveValue<bool>() ?? false))
        {
            Logger.LogWarning("demos are on but tv_enable is 0 - no demo will be recorded. tv_enable 1 "
                + "belongs in server.cfg, where it is read before the map loads (setting it live leaves "
                + "GOTV on port 0 and recording nothing), or turn demos off in Gpp.json.");
            return;
        }

        _demosBefore = EngineDemos();
        _demoArmed = true;
        Server.ExecuteCommand("tv_autorecord 1");
        Logger.LogInformation("Demo armed: tv_autorecord 1 for the map change.");
    }

    // Called from OnMapStart. Autorecord has to go back off - only a pug's own map change is ever meant
    // to record - but *not yet*: the engine starts the recording from a delayed call
    // ("CHLTVServerAsync::ExecuteDelayedCall StartAutoRecordingCall_t"), well after OnMapStart, and a 0
    // written before that call fires cancels the recording instead of leaving it running. Measured: a
    // whole bot pug recorded nothing with the 0 sent at map start, and the same sequence recorded 442 KB
    // with the 0 sent half a minute later.
    //
    // So the disarm waits out the delayed call. The cost of the wait is that a second map load inside it
    // would also record; nothing in the pug flow does that, and an extra demo is a cheaper failure than
    // no demo at all.
    // [SOURCE: measured on this server 2026-11-07]
    private const float AutorecordDisarmSeconds = 30.0f;

    private void DemoMapStarted()
    {
        bool armed = _demoArmed;
        _demoArmed = false;

        if (!armed)
        {
            // Not a pug's load: make sure nothing is left armed from an abandoned one.
            Server.ExecuteCommand("tv_autorecord 0");
            return;
        }

        _recording = true;
        _demoFile = "";
        Logger.LogInformation("Recording this map's demo; it will be stopped and named when the match ends.");
        AddTimer(AutorecordDisarmSeconds, () => Server.ExecuteCommand("tv_autorecord 0"));
    }

    private void StopDemo(float delaySeconds)
    {
        _demoArmed = false;
        if (!_recording) return;

        if (delaySeconds <= 0)
        {
            FinishRecording();
            return;
        }

        AddTimer(delaySeconds, () =>
        {
            if (_recording) FinishRecording();
        });
    }

    private void FinishRecording()
    {
        _recording = false;
        string name = DemoName();
        Server.ExecuteCommand("tv_stoprecord;tv_autorecord 0");
        Logger.LogInformation("Stopped recording; collecting the demo as {Name}.dem", name);
        CollectDemo(name, attempt: 0, lastSize: -1);
    }

    // The engine writes the file after tv_stoprecord, asynchronously, so this waits for a file that is
    // both new and no longer growing. Twenty tries at a second apiece: a long match's demo is tens of MB
    // and lands well inside that.
    private void CollectDemo(string name, int attempt, long lastSize)
    {
        _demoCollectTimer?.Kill();
        _demoCollectTimer = AddTimer(1.0f, () =>
        {
            _demoCollectTimer = null;
            string? written = DemoFiles.PickNew(_demosBefore, EngineDemos(), File.GetLastWriteTimeUtc);

            if (written == null)
            {
                if (attempt < 20)
                {
                    CollectDemo(name, attempt + 1, lastSize);
                    return;
                }
                Logger.LogWarning("The match ended but GOTV wrote no demo into {Directory}. A recording "
                    + "that starts anywhere but a level load captures nothing - check that the pug's map "
                    + "change happened and that tv_enable was 1 before it.", EngineDemoDirectory);
                return;
            }

            long size = Size(written);
            // Still being written: wait for the size to repeat before moving it.
            if (size < 0 || size != lastSize)
            {
                if (attempt < 20)
                {
                    CollectDemo(name, attempt + 1, size);
                    return;
                }
                Logger.LogWarning("{Path} is still growing after {Attempts}s - leaving it where it is.",
                    written, attempt);
                return;
            }

            MoveDemo(written, name, size);
        });
    }

    private static long Size(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void MoveDemo(string written, string name, long size)
    {
        string target = Path.Join(Server.GameDirectory, "csgo", Config.Match.DemoPath.Trim('/'),
            name + ".dem").Replace('\\', '/');
        try
        {
            string? directory = Path.GetDirectoryName(target);
            if (directory != null) Directory.CreateDirectory(directory);
            File.Move(written, target, overwrite: true);
            _demoFile = target;
            Logger.LogInformation("Demo {Path} ({Size} bytes)", target, size);
        }
        catch (Exception exception)
        {
            _demoFile = written;
            Logger.LogError("Could not move {From} to {To}: {Message}", written, target, exception.Message);
        }
    }

    private string DemoName() => MatchRules.DemoFileName(Config.Match.DemoNameFormat, DateTime.Now,
        _matchId, Server.MapName, _team1Name, _team2Name);
}
