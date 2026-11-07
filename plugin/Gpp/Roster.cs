using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Gpp;

// A number per connected player, which is how the warmup toys are aimed. A name is not a usable handle:
// half the room is a Cyrillic flourish and a trademark symbol, and nobody can type "✪ DobackSlaps™" into
// a chat box to slap them.
//
// Two rules, and both of them are about the gap between reading the list and typing a number:
//
//   - Numbers are handed out in connection order and never reused. Somebody leaving does not renumber
//     the room, which is what would make ".fent 4" land on a player who was 5 a second ago.
//   - A number belongs to a SteamID for as long as the plugin is loaded, so a reconnect keeps it. The
//     counter only ever goes up; over a long night the numbers get sparse, which is the point.
//
// A number that nobody here holds is an error, not a near miss. It is never fallen back to a name match:
// the whole reason the number exists is that it is unambiguous, and guessing would hand the slap to a
// bystander.
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

    internal int RosterNumber(CCSPlayerController player)
    {
        NumberPlayer(Id(player));
        return _rosterNumbers[Id(player)];
    }

    // Everyone here, in the order the numbers were handed out.
    private List<CCSPlayerController> NumberedRoster()
    {
        NumberEveryone();
        return HumanPlayers().OrderBy(RosterNumber).ToList();
    }

    // The player holding this number, or null. Only the people actually connected can be hit: a number
    // whose holder has gone home resolves to nobody rather than to whoever inherited the slot.
    private CCSPlayerController? PlayerByNumber(int number) =>
        number <= 0 ? null : HumanPlayers().FirstOrDefault(player => RosterNumber(player) == number);

    // The dot route and the console route both land here, so .players means the same thing whichever way
    // it was typed.
    //
    // During a draft that is two lists, and they are numbered differently on purpose: the pool is
    // numbered 1..n by pick order, which is what !1 takes, and the room is numbered by the roster, which
    // is what .kick takes. Printing one and calling it "the numbers" is how somebody kicks the wrong
    // person, so both go out, each under what it is for.
    private void PlayersOrPool(CCSPlayerController? asker)
    {
        if (_phase == PugPhase.Draft) PrintAvailable(asker);
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
        foreach (var player in roster)
        {
            Tell(asker, $"{Colorize("{lime}")}{RosterNumber(player)}{Colorize("{default}")} "
                + $"{player.PlayerName}{(player.IsBot ? " (bot)" : "")}");
        }
    }
}
