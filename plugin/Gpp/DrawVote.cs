using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// Whether a level match goes to overtime is the room's call, asked one round before it can happen.
//
// A pug that ends 12-12 at one in the morning and then plays another six rounds is a pug nobody wanted,
// and the engine gives no say in it: mp_overtime_enable is read when the round that levels the score
// ends, and by then it is too late to ask. So the question goes up at 12-11 - the only score a single
// round can draw from - and the answer is written to the cvar while the round is still being played.
//
// Overtime is the same shape, so the same question comes back at 15-14 and 18-17: MatchRules.DrawNextRound
// knows where the draws are and is under test, because an off-by-one here is a match that ends when it
// should not have.
//
// Nobody votes, or the room splits evenly: it is a draw. That is the answer that stops, and the one the
// question was asked for.
//
// The cvar is written on every vote rather than once at the end, because the round can finish before the
// clock does and the engine reads mp_overtime_enable the moment the score goes level - a close that waits
// for the timer would be a frame or two late, which is a whole overtime nobody asked for.
//
// It is a pause, the same shape as the Discord hold at round one: mp_pause_match lands at the end of the
// round and holds the freeze time of the round being asked about, so nobody plays it before the room has
// decided what it is for. Like that hold it is the plugin's pause, not a team's - it ignores
// pauses_enabled and counts against nobody's technical pauses - and it ends the moment everyone has
// answered rather than running the clock out. Unpausing hands the freeze time back where it left off.
public partial class Gpp
{
    // The score a draw would land on, once the room has been asked about it. Keeps the question from
    // being asked twice for the same one.
    private int _drawVoteAsked;

    // SteamID to "play it out", for everyone who answered.
    private readonly Dictionary<ulong, bool> _drawVotes = [];
    private int _drawVoteSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _drawVoteTimer;

    private bool DrawVoteOpen => _drawVoteTimer != null;

    private void HookDrawVote()
    {
        // Asked at the start of the round that can draw, not the end of the one before it, because that
        // is where a pause lands: mp_pause_match holds the freeze time of the round it is called in.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            if (_matchPhase == MatchPhase.Live) AfterTick(OpenDrawVoteIfNeeded);
            return HookResult.Continue;
        }, HookMode.Post);

        // A vote somehow still open when the round ends has run out of round rather than out of time.
        // Whatever the room has said by now is the answer, and the cvar already carries it.
        RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            if (DrawVoteOpen) AfterTick(() => CloseDrawVote("the round ended"));
            return HookResult.Continue;
        }, HookMode.Post);
    }

    // Called at go-live: a new match starts with the overtime the config asked for, and with nothing
    // remembered about the last one.
    private void ResetDrawVote()
    {
        _drawVoteTimer?.Kill();
        _drawVoteTimer = null;
        _drawVotes.Clear();
        _drawVoteAsked = 0;
    }

    private void OpenDrawVoteIfNeeded()
    {
        if (!Config.Match.DrawVote || _matchPhase != MatchPhase.Live) return;

        var (score1, score2) = Score();
        int draw = MatchRules.DrawNextRound(
            score1, score2,
            ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24,
            ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6);

        if (draw == 0 || draw == _drawVoteAsked) return;

        _drawVoteAsked = draw;
        _drawVotes.Clear();
        _drawVoteSecondsLeft = Math.Max(5, Config.Match.DrawVoteSeconds);

        // Nobody has said anything yet, so the standing answer is the one that stops.
        ApplyDrawVote();

        Server.ExecuteCommand("mp_pause_match");
        Logger.LogInformation("Draw vote: {Seconds}s paused at {Draw}-{Behind}.",
            _drawVoteSecondsLeft, draw, draw - 1);
        Announce($"This round can make it {Colorize("{green}")}{draw}-{draw}{Colorize("{default}")}.");
        Prompt("Overtime, or call it a draw?", "!1 play it out  -  !2 draw", Everybody);

        _drawVoteTimer = AddTimer(1.0f, DrawVoteTick, TimerFlags.REPEAT);
        DrawVotePanel();
    }

    private void DrawVoteTick()
    {
        if (_matchPhase != MatchPhase.Live)
        {
            AfterTick(() => CloseDrawVote("the match is not live any more"));
            return;
        }

        _drawVoteSecondsLeft--;

        // Everyone connected has answered: there is nobody left to wait for, so the clock is the
        // backstop rather than the gate. Anyone in the server votes - a pug has no roster the server
        // itself believes in, and the people watching are in the room too.
        if (EveryoneAnswered())
        {
            AfterTick(() => CloseDrawVote("everybody answered"));
            return;
        }

        if (_drawVoteSecondsLeft <= 0)
        {
            AfterTick(() => CloseDrawVote("time is up"));
            return;
        }

        DrawVotePanel();
    }

    private bool EveryoneAnswered()
    {
        var here = AnsweringPlayers();
        return here.Count > 0 && here.All(player => _drawVotes.ContainsKey(Id(player)));
    }

    // The board everything else in the pug is drawn on: the score a draw would land on, the two answers
    // with their counts, and the clock.
    private void DrawVotePanel()
    {
        if (!Config.Hud || !DrawVoteOpen) return;
        _repaint = DrawVotePanel;
        WorldDrawVotePanel();
    }

    // A split room stops: overtime is the thing being asked for, so it needs more than half of those who
    // answered. Written to the cvar as soon as it changes, and returns what it wrote.
    private bool ApplyDrawVote()
    {
        var (playOn, draw) = DrawVoteTally();
        bool overtime = playOn > draw;
        Server.ExecuteCommand($"mp_overtime_enable {(overtime ? 1 : 0)}");
        return overtime;
    }

    private (int PlayOn, int Draw) DrawVoteTally()
    {
        int playOn = _drawVotes.Count(vote => vote.Value);
        return (playOn, _drawVotes.Count - playOn);
    }

    // The answer to !1 or !2 while the question is up. Returns whether it was one.
    internal bool AnswerDrawVote(CCSPlayerController player, string number)
    {
        if (!DrawVoteOpen || player.IsBot) return false;
        if (number != "1" && number != "2") return false;

        bool playOn = number == "1";
        _drawVotes[Id(player)] = playOn;
        ApplyDrawVote();
        Tell(player, playOn ? "Overtime it is, if the room agrees." : "A draw, if the room agrees.");
        return true;
    }

    private void CloseDrawVote(string why)
    {
        if (!DrawVoteOpen) return;

        _drawVoteTimer?.Kill();
        _drawVoteTimer = null;

        var (playOn, draw) = DrawVoteTally();
        bool overtime = ApplyDrawVote();
        Clear();
        Server.ExecuteCommand("mp_unpause_match");
        Logger.LogInformation("Draw vote at {Score}-{Score}: play on {PlayOn}, draw {Draw} ({Why}) - "
            + "mp_overtime_enable {Value}.", _drawVoteAsked, _drawVoteAsked, playOn, draw, why, overtime ? 1 : 0);

        Announce(overtime
            ? $"{Colorize("{green}")}Overtime{Colorize("{default}")} if this round levels it ({playOn}-{draw})."
            : $"{Colorize("{green}")}A draw{Colorize("{default}")} if this round levels it ({draw}-{playOn}).");
    }
}
