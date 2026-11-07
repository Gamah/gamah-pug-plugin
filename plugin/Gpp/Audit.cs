using System.Reflection;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Gpp;

// Who typed what. The plugin logged what *happened* - a side picked, a match ended, a demo written - and
// never who asked for it, which is only a gap for the commands that act on somebody else: .kick first,
// but .endmatch, .forceready and .map too. One line per command, in GPP's own log rather than the
// journal, because journald splices the game's stdout mid-word.
//
// Nothing here runs on a tick or on a timer. The chat route is the say listener that was already there,
// and the console route is one listener per css_ command, registered once at load - so the cost is a
// dictionary lookup and a log line on the frames where somebody actually typed something.
public partial class Gpp
{
    // The last thing each player was audited saying. The same command reaches here twice: "!ready" is seen
    // by the say listener, and again as css_ready when CounterStrikeSharp dispatches its own chat trigger.
    // Normalising both to "ready" and dropping a repeat inside a few frames is what keeps one command to
    // one line, whichever route it came by.
    private readonly Dictionary<ulong, (string What, DateTime When)> _lastAudited = [];

    private void HookCommandAudit()
    {
        // Found from the plugin's own [ConsoleCommand] attributes rather than a list kept by hand: a
        // command added later is audited by having been registered, and a list would be the thing that
        // goes stale the one time it matters.
        foreach (string name in AuditedCommandNames())
        {
            AddCommandListener(name, OnAuditedCommand, HookMode.Post);
        }
    }

    internal static List<string> AuditedCommandNames() =>
        typeof(Gpp)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(method => method.GetCustomAttributes<ConsoleCommandAttribute>())
            .Select(attribute => attribute.Command)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

    private HookResult OnAuditedCommand(CCSPlayerController? player, CommandInfo command)
    {
        // A retired instance is still listening until the server restarts - see Unload. Its line would be
        // the second and sixth copy of the same one.
        if (Retired) return HookResult.Continue;
        Audit(player, command.GetCommandString);
        return HookResult.Continue;
    }

    // The one line. Called from the say listener for the dot route and from the console listener for
    // everything else, including rcon - which is the caller with nobody to name.
    internal void Audit(CCSPlayerController? caller, string what)
    {
        what = what.Trim();
        if (what.Length == 0) return;

        ulong who = caller != null && caller.IsValid ? Id(caller) : 0;
        if (SeenJustNow(who, Audits.Normalise(what))) return;

        Logger.LogInformation("cmd: {Who} \"{What}\" [{Phase}]", AuditName(caller), what, AuditPhase());
    }

    private bool SeenJustNow(ulong who, string what)
    {
        var now = DateTime.UtcNow;
        if (_lastAudited.TryGetValue(who, out var last)
            && last.What == what
            && (now - last.When).TotalMilliseconds < Audits.RepeatWindowMs)
        {
            return true;
        }
        _lastAudited[who] = (what, now);
        return false;
    }

    // The SteamID is the half of this that is still worth something in six weeks: names change, and two
    // people can wear the same one on purpose.
    private string AuditName(CCSPlayerController? caller) =>
        caller == null || !caller.IsValid
            ? "console"
            : caller.IsBot ? $"{caller.PlayerName}<bot>" : $"{caller.PlayerName}<{caller.SteamID}>";

    // What the server was doing at the time, which is most of why a line is worth reading back.
    private string AuditPhase() => MatchRunning ? $"{_phase}/{_matchPhase}" : _phase.ToString();
}
