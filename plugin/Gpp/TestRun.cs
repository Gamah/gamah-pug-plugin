using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// !pug test - the whole bot harness in one command, where it used to be a shell script on the box
// (rig-bots.sh: tail the log, read css_teamdump over rcon, sv_cheats 1 / bot_kill / sv_cheats 0 every
// few seconds). Everything that needs doing is inside the server, so none of that had any business
// being outside it.
//
// It does two things. The room is filled to ten with bots, counted as players for the draft, so one
// person can walk the whole flow. And it asks the caller whether the bots should be swept every round -
// which is how a thirteen-round match gets played in the time it takes to read the panel, and how a
// screenshot of a 13-0 gets taken on purpose.
public partial class Gpp
{
    // A pug is ten. Fewer humans than that is made up with bots; ten or more and it adds none.
    private const int TestRunSize = 10;

    // Bots count as players while a test run is on, whatever count_bots says in the config - the config
    // key stays as the thing that survives a reload, and this is the thing an admin turns on for a night.
    private bool _testRun;

    // Sweep the bots every round, and whose side they die for.
    private bool _testSweep;
    private ulong _testCaller;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _testAskTimer;
    private int _testAskSecondsLeft;

    internal bool TestRunning => _testRun;

    // Bots are players when the config says so, or while a test run is on.
    internal bool BotsCountAsPlayers => Config.CountBots || _testRun;

    internal void StartTestRun(CCSPlayerController? caller)
    {
        _testRun = true;
        _testSweep = false;
        _testCaller = caller == null ? 0 : Id(caller);

        TopUpToTestRunSize();
        Logger.LogInformation("Test run on - filling to {Size} with bots, asked for by {Who}.",
            TestRunSize, _testCaller == 0 ? "console" : Name(_testCaller));

        // Console asked, so there is nobody to show a panel to: no sweeping, just the bots.
        if (caller == null || !caller.IsValid)
        {
            Announce($"Test run: filling to {TestRunSize} with bots. !pug test sweep turns the "
                + "every-round sweep on.");
            return;
        }

        AskAboutSweeping();
    }

    internal void StopTestRun()
    {
        if (!_testRun && _testSweep == false && _testAskTimer == null) return;

        _testAskTimer?.Kill();
        _testAskTimer = null;
        _testRun = false;
        _testSweep = false;
        _testCaller = 0;
        KeepTestBotsTopped(0);
        Logger.LogInformation("Test run off - bots gone.");
    }

    // Fill to ten, recounted each time: somebody connecting during a test run should take a bot's place
    // rather than make an eleventh player.
    internal void TopUpToTestRunSize()
    {
        int humans = HumanPlayers().Count(player => !player.IsBot);
        KeepTestBotsTopped(Math.Max(0, TestRunSize - humans));
    }

    // --- the question ------------------------------------------------------------------------------

    private void AskAboutSweeping()
    {
        _testAskSecondsLeft = 30;
        _testAskTimer?.Kill();
        _testAskTimer = AddTimer(1.0f, TestAskTick, TimerFlags.REPEAT);
        TestAskPanel();
    }

    private void TestAskTick()
    {
        // Only while there is nothing else on the screen: the vote, the draft and the match all want the
        // panel more than this does, and the answer is still !1 or !2 in chat either way.
        if (--_testAskSecondsLeft <= 0)
        {
            AnswerTestAsk(sweep: false, timedOut: true);
            return;
        }
        TestAskPanel();
    }

    // The answer, from !1 or !2 typed by whoever called the test run.
    internal bool AnswerTestRun(CCSPlayerController player, string number)
    {
        if (_testAskTimer == null || Id(player) != _testCaller) return false;
        if (number != "1" && number != "2") return false;

        AnswerTestAsk(sweep: number == "1", timedOut: false);
        return true;
    }

    private void AnswerTestAsk(bool sweep, bool timedOut)
    {
        _testAskTimer?.Kill();
        _testAskTimer = null;
        _testSweep = sweep;

        if (sweep)
        {
            Announce($"Test run: bots get killed every round, {Name(_testCaller)}'s side last. "
                + "!pug test off when you are done.");
            // A killed round is as long as it takes to end it, so the freeze in front of it is the whole
            // round. live.cfg puts the real freeze time back at the next go-live.
            Server.ExecuteCommand("mp_freezetime 1");
        }
        else
        {
            Announce(timedOut
                ? $"Test run: {TestRunSize} with bots, nobody being killed."
                : "Test run: bots stay in. !pug test sweep if you change your mind.");
        }
        _keepCentred = false;
        WorldHide();
        StopHud();

        // The test is the whole suite, not the bots half of it: there is nothing to type !pug start for.
        StartThePugWhenTheBotsAreIn();
    }

    private void TestAskPanel()
    {
        if (!Config.Hud) return;
        var caller = PlayerBySteamId(_testCaller);
        if (caller == null || !caller.IsValid) return;

        _repaint = TestAskPanel;
        string panel = WorldLayout.MainPanel(
            $"TEST RUN - SERVER FILLS WITH BOTS TO {TestRunSize} PLAYERS",
            [
                WorldLayout.Section.Of("KILL BOTS EACH ROUND?", "!1  YES     !2  NO"),
            ],
            WorldLayout.Footer($"{_testAskSecondsLeft}s"),
            _hudTicks);
        WorldShow(caller, panel);
    }

    // Even sides, because a test run is the only time nobody is watching the scoreboard: the draft puts
    // ten players on two teams, and then the engine adds a bot of its own, or a human connects into one
    // side, and the match is a 6v4 that nobody notices until the screenshot. Bots are the ones moved -
    // they are the ones who do not mind - and only during the freeze at the start of a round.
    internal void BalanceTestBots()
    {
        if (!_testRun) return;

        var ct = HumanPlayers().Where(player => player.Team == CsTeam.CounterTerrorist).ToList();
        var t = HumanPlayers().Where(player => player.Team == CsTeam.Terrorist).ToList();

        while (Math.Abs(ct.Count - t.Count) > 1)
        {
            bool fromCt = ct.Count > t.Count;
            var from = fromCt ? ct : t;
            var to = fromCt ? t : ct;

            var bot = from.FirstOrDefault(player => player.IsBot);
            if (bot == null) break; // only humans left on the big side: not ours to move

            bot.ChangeTeam(fromCt ? CsTeam.Terrorist : CsTeam.CounterTerrorist);
            from.Remove(bot);
            to.Add(bot);
            Logger.LogInformation("Test run: moved bot {Name} to even the sides ({Ct} CT / {T} T).",
                bot.PlayerName, ct.Count, t.Count);
        }
    }

    // The bots connect at bot_add's pace rather than ours, and a pug started before they are in is a
    // 2v2 with eight bots walking into it. So: wait for the room to be full, give up after ten seconds
    // and start with whoever is there, and go through the same door !pug start goes through.
    private void StartThePugWhenTheBotsAreIn()
    {
        int waited = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(1.0f, () =>
        {
            if (!_testRun || _phase != PugPhase.Idle || MatchRunning)
            {
                timer?.Kill();
                return;
            }

            bool full = HumanPlayers().Count() >= TestRunSize;
            if (!full && ++waited < 10) return;

            timer?.Kill();
            Logger.LogInformation("Test run: starting the pug with {Here} players.", HumanPlayers().Count());
            if (!OpenCaptainOptIn(() => StartMapVote(force: false), force: false)) StartMapVote(force: false);
        }, TimerFlags.REPEAT);
    }

    // Two bot captains is a draft nobody can take part in: a bot neither knifes nor picks, so the clock
    // runs out twice and the teams are drawn for you. One of them is kicked instead, which hands that
    // armband on - GPP reassigns a captain who is no longer here - and the top-up puts a bot back in as
    // somebody who can be picked rather than somebody who has to pick.
    internal void DropOneBotCaptain()
    {
        if (!_testRun) return;

        var first = PlayerBySteamId(_captain1);
        var second = PlayerBySteamId(_captain2);
        if (first == null || second == null || !first.IsBot || !second.IsBot) return;

        Logger.LogInformation("Test run: both captains are bots - kicking {Name} and giving the armband "
            + "to {Who}.", second.PlayerName, _testCaller == 0 ? "whoever is drawn" : Name(_testCaller));
        Server.ExecuteCommand($"bot_kick {second.PlayerName}");

        // Handed over rather than redrawn: with one human in a room of bots, a redraw is nine chances in
        // ten of the same problem. The kick is what keeps the headcount honest - the top-up puts a bot
        // back in as somebody who can be picked.
        _captain2 = _testCaller != 0 && _testCaller != _captain1 ? _testCaller : 0;
    }

    // --- the sweep ---------------------------------------------------------------------------------

    // The round has started, but the room may still be reading "STFU AND MOVE" - the opening pause holds
    // round one while the losing side gets into its Discord channel, and sweeping the bots through that
    // ends the round nobody has played yet. So the sweep waits for the panel to come down, and gives up
    // after a pause's worth of seconds rather than hanging around for a round that is long over.
    internal void SweepTestBotsWhenReady()
    {
        if (!_testSweep) return;

        int tries = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(1.0f, () =>
        {
            if (++tries > 60 || !_testSweep || _matchPhase != MatchPhase.Live)
            {
                timer?.Kill();
                return;
            }
            if (OpeningPauseRunning) return;

            timer?.Kill();
            SweepTestBots();
        }, TimerFlags.REPEAT);
    }

    // Every bot on the side the caller is not on, killed where it stands, which ends the round.
    internal void SweepTestBots()
    {
        if (!_testSweep || OpeningPauseRunning) return;

        var caller = PlayerBySteamId(_testCaller);
        CsTeam keep = caller != null && caller.IsValid ? caller.Team : CsTeam.None;

        int killed = 0;
        foreach (var player in HumanPlayers())
        {
            if (!player.IsBot) continue;
            if (keep != CsTeam.None && player.Team == keep) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;

            // CommitSuicide rather than bot_kill: bot_kill is cheat-flagged, and the shell rig had to
            // turn sv_cheats on and off around every sweep to get at it.
            player.CommitSuicide(explode: false, force: true);
            killed++;
        }

        if (killed > 0) Logger.LogInformation("Test sweep: {Killed} bot(s) killed.", killed);
    }
}
