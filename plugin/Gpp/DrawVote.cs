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
        RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            if (_matchPhase != MatchPhase.Live) return HookResult.Continue;

            // A vote still open when the next round ends has run out of round, not out of time: whatever
            // the room has said by now is the answer, and it has to be written before the engine looks.
            if (DrawVoteOpen) AfterTick(() => CloseDrawVote("the round ended"));
            else AfterTick(OpenDrawVoteIfNeeded);
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

        Announce($"Next round can make it {Colorize("{green}")}{draw}-{draw}{Colorize("{default}")}.");
        Prompt("Overtime, or call it a draw?", "!1 play it out  -  !2 draw", Everybody);
        _drawVoteTimer = AddTimer(1.0f, DrawVoteTick, TimerFlags.REPEAT);
    }

    private void DrawVoteTick()
    {
        _drawVoteSecondsLeft--;

        // Everyone who could answer has: no reason to hold the rest of the clock.
        if (_drawVotes.Count >= AnsweringPlayers().Count)
        {
            AfterTick(() => CloseDrawVote("everybody answered"));
            return;
        }

        if (_drawVoteSecondsLeft <= 0)
        {
            AfterTick(() => CloseDrawVote("time is up"));
            return;
        }

        if (_drawVoteSecondsLeft % 10 == 0)
        {
            var (playOn, draw) = DrawVoteTally();
            Announce($"Overtime {playOn} - draw {draw}. {_drawVoteSecondsLeft}s. !1 play it out, !2 draw.");
        }
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
        Logger.LogInformation("Draw vote at {Score}-{Score}: play on {PlayOn}, draw {Draw} ({Why}) - "
            + "mp_overtime_enable {Value}.", _drawVoteAsked, _drawVoteAsked, playOn, draw, why, overtime ? 1 : 0);

        Announce(overtime
            ? $"{Colorize("{green}")}Overtime{Colorize("{default}")} if this round levels it ({playOn}-{draw})."
            : $"{Colorize("{green}")}A draw{Colorize("{default}")} if this round levels it ({draw}-{playOn}).");
    }
}
