using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Gpp;

// A number per connected player, which is how the warmup toys are aimed. A name is not a usable handle:
// half the room is a Cyrillic flourish and a trademark symbol, and nobody can type "✪ DobackSlaps™" into
// a chat box to slap them.
//
// The number is the position in the list that was just printed: 1..n, top to bottom, the same reading as
// the draft's pool. One numbering for the whole plugin was the call - two of them, with a .players that
// printed one and a .kick that took the other, is how somebody kicks the wrong person.
//
// The cost is paid in the gap between reading the list and typing a number: somebody leaving renumbers
// everyone below them, so a number read a moment ago can land one player off. The ordering underneath is
// still connection order, kept per SteamID and never reshuffled, so the list only changes where it has
// to - and a reconnect comes back where they were rather than at the bottom.
//
// A number nobody holds is an error, not a near miss. It is never fallen back to a name match: the whole
// reason the number exists is that it is unambiguous, and guessing would hand the slap to a bystander.
public partial class Gpp
{
    private readonly Dictionary<ulong, int> _rosterNumbers = [];
    private int _nextRosterNumber = 1;

    // Called when a client is put in the server, and swept over the room on load and on a map start for
    // whoever was already standing there - a plugin loads after the players it loads into.
    private void NumberPlayer(ulong id)
    {
        if (id == 0 || _rosterNumbers.ContainsKey(id)) return;
        _rosterNumbers[id] = _nextRosterNumber++;
    }

    // Slot order for the backfill, which is the closest thing to connection order the engine still has by
    // the time the plugin is asking.
    private void NumberEveryone()
    {
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV).OrderBy(p => p.Slot))
        {
            NumberPlayer(Id(player));
        }
    }

    // Not shown to anybody: this is the sort key, which is what keeps the list in connection order and
    // stops it reshuffling under somebody who is reading it.
    private int JoinOrder(CCSPlayerController player)
    {
        NumberPlayer(Id(player));
        return _rosterNumbers[Id(player)];
    }

    // Everyone here, in the order they arrived. The position in this list is the number, so this is the
    // one place the ordering is decided - the list that is printed and the list a number is resolved
    // against are the same call.
    private List<CCSPlayerController> NumberedRoster()
    {
        NumberEveryone();
        return HumanPlayers().OrderBy(JoinOrder).ToList();
    }

    // The player at this position, or null. Off the end is nobody rather than the last person in the
    // room: a mistyped number must not hit a bystander.
    private CCSPlayerController? PlayerByNumber(int number)
    {
        var roster = NumberedRoster();
        return number >= 1 && number <= roster.Count ? roster[number - 1] : null;
    }

    // The dot route and the console route both land here, so .players means the same thing whichever way
    // it was typed.
    //
    // A draft is the pool and nothing else, the way it has always been: the list in front of the captains
    // is the one they are picking off, and a second list under it is a second set of numbers to get wrong.
    private void PlayersOrPool(CCSPlayerController? asker)
    {
        if (_phase == PugPhase.Draft)
        {
            PrintAvailable(asker);
            return;
        }
        PlayersCommand(asker);
    }

    private void PlayersCommand(CCSPlayerController? asker)
    {
        // No phase fence. The list used to be fenced off with the warmup toys, because the numbers only
        // aimed those - which left .kick, the one command meant for a live pug, with nothing to read a
        // number off exactly when it was needed. The reply goes to the asker alone, so a list during a
        // live round is not a word to anybody else.
        var roster = NumberedRoster();
        if (roster.Count == 0)
        {
            Tell(asker, "Nobody here.");
            return;
        }

        // Chat rather than a panel: it is a list somebody is about to read a number off and type, and a
        // panel is gone by the time they have finished typing. Scrollback is the right shape for it.
        // The toys are warmup-only, so promising them mid-match would be copy that lies about what the
        // number does.
        bool warmup = _phase == PugPhase.Idle && InWarmupPeriod() && !MatchRunning;
        Tell(asker, warmup
            ? $"{roster.Count} here - the number is what .fent, .drug, .slap, .kill and .kick take:"
            : $"{roster.Count} here - the number is what .kick takes:");
        // The position in this list is the number, and PlayerByNumber reads it off the same ordering.
        for (int index = 0; index < roster.Count; index++)
        {
            var player = roster[index];
            Tell(asker, $"{Colorize("{lime}")}{index + 1}{Colorize("{default}")} "
                + $"{player.PlayerName}{(player.IsBot ? " (bot)" : "")}");
        }
    }
}
