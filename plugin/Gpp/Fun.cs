using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// Warmup toys: .kill, .slap, .fent and .drug.
//
// Two things about how they are wired. First, the dot: CounterStrikeSharp's chat triggers are "!" and "/"
// (configs/core.json), so nothing turns ".kill" into css_kill - registering css_kill answers !kill and
// never .kill. A "say" listener of our own is what makes the dot work without editing core.json, which
// needs a server restart to take. MatchZy served its own "." commands the same way, from a chat hook.
//
// Second, nothing here does its work inline. A chat command runs inside the engine's netchan message
// processing, which has a CPU budget per call and drops the client whose message overran it - that is how
// the player who typed !pug start was disconnected with NETWORK_DISCONNECT_OVERFLOW while the vote they
// started came up fine for everyone else. Every handler below hands the work to the next frame.
public partial class Gpp
{
    private void HookFunCommands()
    {
        AddCommandListener("say", OnSay, HookMode.Post);
        AddCommandListener("say_team", OnSay, HookMode.Post);
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        if (command.ArgCount < 2) return HookResult.Continue;

        string message = command.GetArg(1).Trim();
        if (message.Length < 2 || (message[0] != '.' && message[0] != '!')) return HookResult.Continue;

        // A bare number is the whole command during a vote or a draft: !3 votes for map 3, and !3 picks
        // player 3 when it is your turn. It goes through the say listener rather than a console command
        // because there is no css_3 to register - CSSharp turns !foo into css_foo, and nobody is going
        // to register one of those per number.
        if (message[1..].All(char.IsDigit))
        {
            string number = message[1..];
            AfterTick(() => NumberCommand(PlayerBySteamId(Id(player)), number));
            return HookResult.Continue;
        }

        // Past here only the dot commands; ! is CSSharp's own trigger and it has already handled it.
        if (message[0] != '.') return HookResult.Continue;

        var parts = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HookResult.Continue;

        string verb = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : "";
        ulong who = Id(player);

        switch (verb)
        {
            // The match commands. These are console commands as well, so !ready works too, but the dot is
            // what everyone types - it is what MatchZy answered for years - and "." is still not one of
            // CounterStrikeSharp's chat triggers (configs/core.json: "!" and "/"), so nothing turns
            // ".ready" into css_ready. This listener is the only route for it.
            case "captain":
                AfterTick(() => Volunteer(PlayerBySteamId(who)));
                return HookResult.Continue;
            // .gaben readies up. It is a joke with a straight face: an alias nobody has to be taught
            // twice, which is more than can be said for most of the commands here.
            case "gaben":
            case "ready":
            case "r":
                AfterTick(() => Ready(PlayerBySteamId(who)));
                return HookResult.Continue;
            case "unready":
            case "notready":
            case "ur":
                AfterTick(() => Unready(PlayerBySteamId(who)));
                return HookResult.Continue;
            case "forceready":
            case "forcestart":
                AfterTick(() =>
                {
                    var caller = PlayerBySteamId(who);
                    if (IsAdmin(caller)) ForceReady();
                    else Tell(caller, "You do not have access to this command.");
                });
                return HookResult.Continue;
            case "stay":
                AfterTick(() => ChooseSide(PlayerBySteamId(who), stay: true));
                return HookResult.Continue;
            case "switch":
            case "swap":
                AfterTick(() => ChooseSide(PlayerBySteamId(who), stay: false));
                return HookResult.Continue;
            case "pause":
                AfterTick(() => PauseMatch(PlayerBySteamId(who), technical: false));
                return HookResult.Continue;
            case "tech":
                AfterTick(() => PauseMatch(PlayerBySteamId(who), technical: true));
                return HookResult.Continue;
            case "unpause":
                AfterTick(() => AskToUnpause(PlayerBySteamId(who)));
                return HookResult.Continue;
            case "endmatch":
                AfterTick(() =>
                {
                    var caller = PlayerBySteamId(who);
                    if (IsAdmin(caller)) AbandonMatch($"{caller?.PlayerName ?? "An admin"} ended it.");
                    else Tell(caller, "You do not have access to this command.");
                });
                return HookResult.Continue;
            case "kill":
                AfterTick(() => KillCommand(PlayerBySteamId(who)));
                return HookResult.Continue;
            case "slap":
                AfterTick(() => SlapCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "fent":
                AfterTick(() => FentCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "drug":
                AfterTick(() => DrugCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            default:
                return HookResult.Continue;
        }
    }

    // A bare number means whatever the pug is waiting for. Outside a vote or a draft it is silent -
    // "!1" in chat during a match is somebody talking, not a command that failed.
    private void NumberCommand(CCSPlayerController? player, string number)
    {
        if (player == null || !player.IsValid) return;

        switch (_phase)
        {
            case PugPhase.MapVote:
                CastVote(player, number);
                break;
            case PugPhase.Draft:
                MakePick(player, number);
                break;
        }
    }

    // The same three as console commands, so they answer !kill / /kill and work from rcon.
    [ConsoleCommand("css_kill", "Kill yourself")]
    public void OnKillCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => KillCommand(player));

    [ConsoleCommand("css_slap", "Slap a player (admin, warmup only)")]
    [CommandHelper(minArgs: 0, usage: "<player> [damage]")]
    public void OnSlapCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => SlapCommand(player, arg));
    }

    [ConsoleCommand("css_fent", "Spin a player's view for a few seconds (admin, warmup only)")]
    [CommandHelper(minArgs: 0, usage: "<player> [seconds]")]
    public void OnFentCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => FentCommand(player, arg));
    }

    [ConsoleCommand("css_drug", "Warp a player's camera for a few seconds (admin, warmup only)")]
    [CommandHelper(minArgs: 0, usage: "<player> [seconds]")]
    public void OnDrugCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => DrugCommand(player, arg));
    }

    // .kill is for the player who is stuck in a corner of a warmup map, not a way out of a lost round, and
    // it must not decide a captains' duel either.
    private void KillCommand(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;

        if (_phase != PugPhase.Idle)
        {
            player.PrintToChat(Prefix() + "Not during a pug - wait for the teams.");
            return;
        }
        if (!InWarmupPeriod())
        {
            player.PrintToChat(Prefix() + "Only during warmup.");
            return;
        }
        if (!player.PawnIsAlive)
        {
            player.PrintToChat(Prefix() + "You are already dead.");
            return;
        }

        player.CommitSuicide(explode: false, force: true);
    }

    private void SlapCommand(CCSPlayerController? admin, string arg)
    {
        if (!IsAdmin(admin))
        {
            if (admin != null && admin.IsValid) admin.PrintToChat(Prefix() + "You do not have access to this command.");
            return;
        }
        if (!WarmupToy(admin, out _)) return;

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Tell(admin, "Usage: .slap <player> [damage]");
            return;
        }

        int damage = parts.Length > 1 && int.TryParse(parts[^1], out int parsed) ? Math.Max(0, parsed) : 0;
        string needle = parts.Length > 1 && damage.ToString() == parts[^1]
            ? string.Join(' ', parts[..^1])
            : string.Join(' ', parts);

        var target = FindPlayer(admin, needle);
        if (target == null) return;
        if (!target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            Tell(admin, $"{target.PlayerName} is not alive.");
            return;
        }

        Slap(target, damage);
        Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} got slapped"
            + (damage > 0 ? $" for {damage}." : "."));
    }

    // Velocity goes on through Teleport rather than by assigning to AbsVelocity: the field is networked
    // from the pawn and writing it in place is not replicated, so the player never actually moves.
    private void Slap(CCSPlayerController target, int damage)
    {
        var pawn = target.PlayerPawn.Value;
        if (pawn == null) return;

        var velocity = new Vector(
            pawn.AbsVelocity.X + _rng.Next(-240, 241),
            pawn.AbsVelocity.Y + _rng.Next(-240, 241),
            pawn.AbsVelocity.Z + 300);
        pawn.Teleport(null, null, velocity);

        if (damage <= 0) return;
        if (pawn.Health <= damage)
        {
            target.CommitSuicide(explode: false, force: true);
            return;
        }
        pawn.Health -= damage;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private const int EffectMaxSeconds = 30;

    // .fent spins the view. This is the one that started life called .drug: it teleports the pawn's view
    // angles ten times a second, which is not a camera effect at all - it is the player's transform being
    // yanked around underneath them. Kept because it is funny, named for what it actually feels like.
    // [SOURCE: behaviour of CCSPlayerPawn.Teleport angles, CounterStrikeSharp 1.0.376 - not a documented
    // contract, re-check after a CS2 update.]
    private void FentCommand(CCSPlayerController? admin, string arg)
    {
        var target = EffectTarget(admin, arg, ".fent", out int seconds);
        if (target == null) return;

        StartFent(Id(target), seconds);
        Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} is having a moment ({seconds}s).");
    }

    // .drug is the colour trip, the same effect SourceMod's funcommands had: the screen washes through
    // random colours while the world breathes in and out.
    //
    // The colour comes from the engine's screen-fade layer, addressed per player. CUserMessageFade
    // (UM_Fade, id 106) is in CS2's base usermessages.proto with duration/hold_time/flags/color, and a
    // CSSharp UserMessage carries a RecipientFilter, so one client can be sent a fade nobody else sees.
    // This is not the flashbang overlay: that one whites the screen out and reads as a bug rather than
    // a trip. The breathing is DesiredFOV, a networked field on the controller.
    // [SOURCE: CS2 Protobufs/usermessages.proto (SteamDatabase/GameTracking-CS2) for the field names;
    // the fixed-point scale on duration/hold_time is not documented - 512 per second is Source 1's
    // SCREENFADE_FRACBITS and what the CS2 funcommands ports use. Read 2026-10-17, re-check after a
    // CS2 update.]
    private void DrugCommand(CCSPlayerController? admin, string arg)
    {
        var target = EffectTarget(admin, arg, ".drug", out int seconds);
        if (target == null) return;

        StartDrug(Id(target), seconds);
        Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} is seeing things ({seconds}s).");
    }

    // Shared by both: admin, warmup, "<player> [seconds]", and the player has to be alive.
    private CCSPlayerController? EffectTarget(CCSPlayerController? admin, string arg, string verb, out int seconds)
    {
        seconds = 10;

        if (!IsAdmin(admin))
        {
            if (admin != null && admin.IsValid) admin.PrintToChat(Prefix() + "You do not have access to this command.");
            return null;
        }
        if (!WarmupToy(admin, out _)) return null;

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Tell(admin, $"Usage: {verb} <player> [seconds]");
            return null;
        }

        string needle = string.Join(' ', parts);
        if (parts.Length > 1 && int.TryParse(parts[^1], out int parsed))
        {
            seconds = Math.Clamp(parsed, 1, EffectMaxSeconds);
            needle = string.Join(' ', parts[..^1]);
        }

        var target = FindPlayer(admin, needle);
        if (target == null) return null;
        if (!target.PawnIsAlive)
        {
            Tell(admin, $"{target.PlayerName} is not alive.");
            return null;
        }
        return target;
    }

    // One timer per drugged player, keyed by id so a second .drug on the same person restarts the clock
    // instead of stacking two timers that fight over the same view.
    private readonly Dictionary<ulong, CounterStrikeSharp.API.Modules.Timers.Timer> _fentTimers = new();

    private void StartFent(ulong id, int seconds)
    {
        StopFent(id);

        int ticksLeft = seconds * 10;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(0.1f, () =>
        {
            var player = PlayerBySteamId(id);
            if (--ticksLeft <= 0 || player == null || !player.IsValid || !player.PawnIsAlive
                || _phase != PugPhase.Idle || !InWarmupPeriod())
            {
                // Killing this timer from inside its own tick is the one case CSSharp handles; killing a
                // different one from in here is what segfaults the server (see AfterTick in Gpp.cs).
                timer?.Kill();
                _fentTimers.Remove(id);
                return;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null) return;

            var angles = new QAngle(
                Math.Clamp(pawn.V_angle.X + _rng.Next(-12, 13), -89, 89),
                pawn.V_angle.Y + _rng.Next(-20, 21),
                0);
            pawn.Teleport(null, angles, null);
        }, TimerFlags.REPEAT);

        _fentTimers[id] = timer;
    }

    private void StopFent(ulong id)
    {
        if (!_fentTimers.Remove(id, out var running)) return;
        running.Kill();

        // Level them up again. The spin leaves the view wherever the last nudge put it, which is usually
        // staring at the sky or the floor, and in a match that is somebody's first round ruined.
        var player = PlayerBySteamId(id);
        var pawn = player?.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.Teleport(null, new QAngle(0, pawn.V_angle.Y, 0), null);
    }

    private const uint NormalFov = 90;

    private readonly Dictionary<ulong, CounterStrikeSharp.API.Modules.Timers.Timer> _drugTimers = new();

    private void StartDrug(ulong id, int seconds)
    {
        StopDrug(id);

        int ticksLeft = seconds * 10;
        int tick = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(0.1f, () =>
        {
            var player = PlayerBySteamId(id);
            if (--ticksLeft <= 0 || player == null || !player.IsValid || !player.PawnIsAlive
                || _phase != PugPhase.Idle || !InWarmupPeriod())
            {
                timer?.Kill();
                _drugTimers.Remove(id);
                if (player != null && player.IsValid) SoberUp(player);
                return;
            }

            // A slow breath rather than a strobe: the FOV walks a sine between 50 and 130 over about four
            // seconds, which warps the world without making anyone ill.
            double phase = tick++ / 40.0 * 2 * Math.PI;
            player.DesiredFOV = (uint)(90 + 40 * Math.Sin(phase));
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");

            // A new colour every 0.4s, fading over the same 0.4s, so the washes run into each other.
            // Faster than this is a strobe, which is both unpleasant and worth not inflicting on anyone.
            if (tick % 4 == 0)
            {
                ColorScreen(player, _rng.Next(256), _rng.Next(256), _rng.Next(256), DrugAlpha, 0.4f, 0.4f,
                    FadeOut | FadeStayOut);
            }
        }, TimerFlags.REPEAT);

        _drugTimers[id] = timer;
    }

    private void StopDrug(ulong id)
    {
        if (!_drugTimers.Remove(id, out var running)) return;
        running.Kill();
        var player = PlayerBySteamId(id);
        if (player != null && player.IsValid) SoberUp(player);
    }

    // Puts the camera back. A warped FOV outlives the timer that set it, outlives the round, and would
    // follow the player into a live match - so everything that ends an effect comes through here.
    private static void SoberUp(CCSPlayerController player)
    {
        player.DesiredFOV = NormalFov;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");

        // PURGE on its own clears whatever fade the client is holding. Without it the last colour sent
        // stays on screen for its hold time after the timer has gone, which looks like the effect stuck.
        ColorScreen(player, 0, 0, 0, 0, 0f, 0f, FadePurge);
    }

    // CUserMessageFade flags, from the Source screen-fade set.
    private const int FadeIn = 0x0001;
    private const int FadeOut = 0x0002;
    private const int FadeStayOut = 0x0008;
    private const int FadePurge = 0x0010;

    // Translucent enough to see through and still play. 255 is a blindfold.
    private const int DrugAlpha = 110;

    // duration and hold_time are fixed point, 512 units to the second.
    private const int FadeFracScale = 512;

    private static void ColorScreen(CCSPlayerController player, int r, int g, int b, int alpha,
        float duration, float hold, int flags)
    {
        var message = UserMessage.FromPartialName("Fade");
        message.SetInt("duration", (int)(duration * FadeFracScale));
        message.SetInt("hold_time", (int)(hold * FadeFracScale));
        message.SetInt("flags", flags);
        // fixed32, little endian: red in the low byte, alpha in the high one.
        message.SetInt("color", r | (g << 8) | (b << 16) | (alpha << 24));
        message.Recipients.Add(player);
        message.Send();
    }

    // Nothing from the warmup toys may survive into a pug. Called when a vote or a draft starts, at the
    // handoff, and on every map change: a stuck FOV is invisible to whoever set it and ruins the match
    // for whoever has it.
    internal void ClearFunEffects()
    {
        foreach (var id in _fentTimers.Keys.ToList()) StopFent(id);
        foreach (var id in _drugTimers.Keys.ToList()) StopDrug(id);

        // Timers only know the players they were started on. Anyone who reconnected, or who was drugged
        // before a plugin reload, is caught by sweeping the whole server.
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            if (player.DesiredFOV is not (0 or NormalFov)) SoberUp(player);
        }
    }

    // Both toys are warmup-only: a slap or a swimming view during a live round decides it.
    private bool WarmupToy(CCSPlayerController? admin, out bool ok)
    {
        ok = _phase == PugPhase.Idle && InWarmupPeriod();
        if (!ok) Tell(admin, "Only during warmup, and not while a pug is running.");
        return ok;
    }

    // Exact name, then a single unambiguous partial - the same rule the draft uses for picking players.
    private CCSPlayerController? FindPlayer(CCSPlayerController? admin, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return null;
        var here = HumanPlayers();

        var exact = here.FirstOrDefault(p => p.PlayerName.Equals(needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var partial = here
            .Where(p => p.PlayerName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) return partial[0];

        Tell(admin, partial.Count == 0 ? $"Nobody here matches \"{needle}\"." : $"\"{needle}\" matches {partial.Count} players.");
        return null;
    }

    // An admin on the server gets chat; one working over rcon gets the log, which is all they can see.
    private void Tell(CCSPlayerController? admin, string message)
    {
        if (admin != null && admin.IsValid) admin.PrintToChat(Prefix() + message);
        else Logger.LogInformation("{Message}", message);
    }
}
