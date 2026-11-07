using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace Gpp;

// .kick - admin only, works during a pug, and holds the door shut for as long as that pug is running.
//
// This server is open on purpose: no whitelist, and anyone may walk in mid-match and pick a side. So the
// block is the weakest one that does the job - a set of SteamIDs in memory, enforced only while a pug is
// live, and emptied when the next pug starts. The moment the pug is over they can walk back in, which is
// the point: it is about the match being played, not about the person.
//
// What it is not: a ban. A hot reload is how this plugin is deployed and it loses the set, as does a map
// change or a server restart. A deploy mid-match already costs the match state, so nothing here is worse
// than what it is standing next to.
public partial class Gpp
{
    // Who may not come back, and what they were kicked for. Keyed the way everything else keys players.
    private readonly Dictionary<ulong, (string Name, string Why)> _kicked = [];

    // A pug is on: anything but an idle warmup. Both halves matter - the phases before go-live are a pug
    // being set up, and MatchRunning is the match itself.
    private bool PugLive => _phase != PugPhase.Idle || MatchRunning;

    private void HookKick()
    {
        // Two hooks for one job. Authorized is the first moment a SteamID exists, which is early enough to
        // turn somebody away before Joins puts them in spectator to choose a side; put-in-server is the
        // moment a controller is certainly there to disconnect. Whichever runs first does the work and the
        // other finds them already gone.
        //
        // Both start with a dictionary miss on an empty set, which is what nearly every join is.
        RegisterListener<Listeners.OnClientAuthorized>((slot, id) =>
        {
            if (Retired || _kicked.Count == 0) return;
            if (!_kicked.ContainsKey(id.SteamId64)) return;
            AfterTick(() => TurnAwayAtTheDoor(slot));
        });

        RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            if (Retired || _kicked.Count == 0) return;
            AfterTick(() => TurnAwayAtTheDoor(slot));
        });
    }

    private void TurnAwayAtTheDoor(int slot)
    {
        if (!PugLive || _kicked.Count == 0) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        if (!_kicked.TryGetValue(Id(player), out var kick)) return;

        Logger.LogInformation("kick: turned {Name}<{Id}> away at the door - kicked from this pug ({Why}).",
            player.PlayerName, Id(player), Reason(kick.Why));
        ShowThemOut(player);
    }

    // Both ways out, in the order that works at the awkward moment. A client being turned away at the
    // door is a frame or two into joining, and `kickid` is the engine's own path - it takes a userid and
    // does not care how far in they are. Disconnect is the managed one and is what is left if the userid
    // has not arrived yet. Calling the second after the first has worked is a no-op on a client that has
    // gone, so there is nothing to sequence.
    private static void ShowThemOut(CCSPlayerController player)
    {
        int? userId = player.UserId;
        if (userId.HasValue) Server.ExecuteCommand($"kickid {userId.Value} Kicked from this pug");
        else player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED_VOTEDOFF);
    }

    // Emptied when a pug begins, not when one ends. The gate is PugLive, so between pugs the set does
    // nothing anyway, and clearing at the start means there is one place to clear rather than every path
    // a pug can end by - a missed one of those would be somebody still locked out next week.
    internal void ForgetKicks()
    {
        if (_kicked.Count == 0) return;
        Logger.LogInformation("kick: new pug, so the {Count} kick(s) from the last one are forgotten.",
            _kicked.Count);
        _kicked.Clear();
    }

    private void KickCommand(CCSPlayerController? caller, string arg)
    {
        if (!IsAdmin(caller))
        {
            Tell(caller, "You do not have access to this command.");
            return;
        }

        var (needle, why) = Kicks.Split(arg);
        if (needle.Length == 0)
        {
            Tell(caller, "Usage: .kick <player|number> [reason] - .players for the numbers.");
            return;
        }

        CCSPlayerController? target;
        if (needle.All(char.IsDigit))
        {
            target = int.TryParse(needle, out int number) ? PlayerByNumber(number) : null;
            if (target == null)
            {
                Tell(caller, $"Nobody here is {needle} - .players for the list.");
                return;
            }
        }
        else
        {
            // Says why itself when a name matches nobody or several people.
            target = FindPlayer(caller, needle);
            if (target == null) return;
        }

        if (caller != null && caller.IsValid && Id(target) == Id(caller))
        {
            Tell(caller, "You cannot kick yourself.");
            return;
        }
        if (target.IsBot)
        {
            Tell(caller, "Bots come and go with !pug test - .kick is for people.");
            return;
        }

        ulong who = Id(target);
        string name = target.PlayerName;
        bool held = PugLive;
        if (held) _kicked[who] = (name, why);

        Logger.LogInformation("kick: {Admin} kicked {Name}<{Id}> ({Why}) in {Phase} - rejoin {Held}.",
            AuditName(caller), name, who, Reason(why), AuditPhase(),
            held ? "blocked until this pug is over" : "not blocked, no pug is running");

        Announce(held
            ? $"{name} was kicked{Because(why)} and cannot rejoin until this pug is over."
            : $"{name} was kicked{Because(why)}.");

        // Last, because it is the thing that cannot be taken back: the set is written and the room told
        // before the client is gone, so a kick that races a disconnect still leaves the door shut.
        ShowThemOut(target);
    }

    // The way back in before the pug is over, for the kick that was a mistake or the apology that landed.
    // With no argument it says who is being held, which is the only way to see the set at all.
    private void UnkickCommand(CCSPlayerController? caller, string arg)
    {
        if (!IsAdmin(caller))
        {
            Tell(caller, "You do not have access to this command.");
            return;
        }

        string needle = arg.Trim();
        if (needle.Length == 0)
        {
            if (_kicked.Count == 0)
            {
                Tell(caller, "Nobody is kicked from this pug.");
                return;
            }
            foreach (var (id, kick) in _kicked)
            {
                Tell(caller, $"{kick.Name} <{id}> - {Reason(kick.Why)}");
            }
            Tell(caller, ".unkick <name|steamid> lets one of them back in.");
            return;
        }

        var match = _kicked
            .Where(entry => entry.Key.ToString() == needle
                || entry.Value.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (match.Count == 0)
        {
            Tell(caller, $"Nobody kicked from this pug matches \"{needle}\" - .unkick on its own lists them.");
            return;
        }
        if (match.Count > 1)
        {
            Tell(caller, $"\"{needle}\" matches {match.Count} of the kicked - name them exactly, or by SteamID.");
            return;
        }

        _kicked.Remove(match[0].Key);
        Logger.LogInformation("kick: {Admin} let {Name}<{Id}> back in.",
            AuditName(caller), match[0].Value.Name, match[0].Key);
        Tell(caller, $"{match[0].Value.Name} may rejoin. Nothing tells them so - they have to try again.");
    }

    private static string Reason(string why) => why.Length == 0 ? "no reason given" : why;

    private static string Because(string why) => why.Length == 0 ? "" : $" - {why}";

    [ConsoleCommand("css_kick", "Kick a player, and keep them out for the rest of this pug (admin)")]
    [CommandHelper(minArgs: 0, usage: "<player|number> [reason]")]
    public void OnKickCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => KickCommand(player, arg));
    }

    [ConsoleCommand("css_unkick", "Let a kicked player back into this pug, or list who is out (admin)")]
    [CommandHelper(minArgs: 0, usage: "[name|steamid]")]
    public void OnUnkickCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => UnkickCommand(player, arg));
    }
}
