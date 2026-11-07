using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// .spec - admin only, any phase, and it is a nudge rather than a sentence. Somebody AFK in a live round
// is a man their team does not have; moving them to spectator is the server agreeing with what is
// already true, and pressing M puts them straight back on a side.
//
// Which is the one way it is not like .kick: nothing is remembered. There is no set, nothing to release,
// and nothing watches the door - the moment they come back they are a player again. The only state here
// is the line in the log saying who moved whom, which is the question asked afterwards.
//
// It deliberately does not use _heldInSpectator either: that set is the pug's own parking, swept and
// released by the phases, and a .spec released by the next phase change would be a different command.
// This is the same one-off nudge Joins gives somebody who walks in mid-match.
public partial class Gpp
{
    private void SpecCommand(CCSPlayerController? caller, string arg)
    {
        if (!IsAdmin(caller))
        {
            Tell(caller, "You do not have access to this command.");
            return;
        }

        var (needle, why) = Kicks.Split(arg);
        if (needle.Length == 0)
        {
            Tell(caller, "Usage: .spec <player|number> [reason] - .players for the numbers.");
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

        if (target.IsBot)
        {
            Tell(caller, "Bots come and go with !pug test - .spec is for people.");
            return;
        }
        if (target.Team == CsTeam.Spectator)
        {
            Tell(caller, $"{target.PlayerName} is already in spectator.");
            return;
        }

        // Alive is the normal case for somebody AFK, and a team change takes their pawn with it. Worth a
        // line in the log rather than a surprise: in a live round it is their team playing a man down,
        // which is the thing that was already happening.
        bool wasAlive = target.PawnIsAlive;
        ulong who = Id(target);
        string name = target.PlayerName;

        target.ChangeTeam(CsTeam.Spectator);

        Logger.LogInformation("spec: {Admin} moved {Name}<{Id}> to spectator ({Why}) in {Phase}{Alive}.",
            AuditName(caller), name, who, Reason(why), AuditPhase(),
            wasAlive ? " - they were alive, so their pawn went with it" : "");

        Announce($"{name} was moved to spectator{Because(why)} - press M to take a side again.");
        target.PrintToChat(Prefix() + "You are in spectator. Press M to join a side whenever you are back.");
    }

    [ConsoleCommand("css_spec", "Move a player to spectator - they can rejoin a side at will (admin)")]
    [CommandHelper(minArgs: 0, usage: "<player|number> [reason]")]
    public void OnSpecCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => SpecCommand(player, arg));
    }
}
