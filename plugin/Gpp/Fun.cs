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
// needs a server restart to take.
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

    // The same chat line can reach this listener more than once - a say listener is registered per plugin
    // load, and a hot reload leaves the previous registration live until the server restarts, so after two
    // reloads every dot command answered twice. Rather than depend on how CounterStrikeSharp cleans up,
    // the same player saying the same thing inside a couple of frames is treated as the one message.
    private readonly Dictionary<int, (string Text, DateTime When)> _lastSaid = [];

    private bool AlreadySaid(CCSPlayerController player, string message)
    {
        var now = DateTime.UtcNow;
        if (_lastSaid.TryGetValue(player.Slot, out var last)
            && last.Text == message
            && (now - last.When).TotalMilliseconds < 250)
        {
            return true;
        }
        _lastSaid[player.Slot] = (message, now);
        return false;
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        // An instance that has been unloaded is still registered here until the server restarts - see
        // Unload. Its answer would be the second, third and sixth copy of the same reply.
        if (Retired) return HookResult.Continue;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        if (command.ArgCount < 2) return HookResult.Continue;

        string message = command.GetArg(1).Trim();
        if (AlreadySaid(player, message)) return HookResult.Continue;
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
            // what everyone types, and "." is not one of
            // CounterStrikeSharp's chat triggers (configs/core.json: "!" and "/"), so nothing turns
            // ".ready" into css_ready. This listener is the only route for it.
            // The three answers to the idle-server panel. They do nothing while it is not up.
            case "reload":
            case "restart":
            case "skip":
                AfterTick(() => AnswerIdlePrompt(PlayerBySteamId(who), verb));
                return HookResult.Continue;
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
                AfterTick(() => KillCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "slap":
                AfterTick(() => SlapCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "map":
                AfterTick(() => MapCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "sound":
                AfterTick(() => SoundCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "yes":
                AfterTick(() => AnswerMapRequest(PlayerBySteamId(who), yes: true));
                return HookResult.Continue;
            case "no":
                AfterTick(() => AnswerMapRequest(PlayerBySteamId(who), yes: false));
                return HookResult.Continue;
            case "narcan":
                AfterTick(() => NarcanCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "players":
            case "who":
                AfterTick(() => PlayersOrPool(PlayerBySteamId(who)));
                return HookResult.Continue;
            case "fent":
                AfterTick(() => FentCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "drug":
                AfterTick(() => DrugCommand(PlayerBySteamId(who), rest));
                return HookResult.Continue;
            case "smoke":
            case "smokes":
                AfterTick(() => SmokesCommand(PlayerBySteamId(who), rest));
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

        // The test run's own question, which is asked of one person and is not a phase.
        if (AnswerTestRun(player, number)) return;

        // The draw vote is asked of everyone, mid-match, and is not a phase either.
        if (AnswerDrawVote(player, number)) return;

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

    // The same ones as console commands, so they answer !kill / /kill and work from rcon.
    [ConsoleCommand("css_kill", "Kill yourself, or a player (admin, warmup only)")]
    [CommandHelper(minArgs: 0, usage: "[player]")]
    public void OnKillCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => KillCommand(player, arg));
    }

    [ConsoleCommand("css_sound", "Play a sound event off your own pawn, to find one that works")]
    [CommandHelper(minArgs: 0, usage: "[sound event name]")]
    public void OnSoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => SoundCommand(player, arg));
    }

    [ConsoleCommand("css_slap", "Slap yourself, or a player (admin), warmup only")]
    [CommandHelper(minArgs: 0, usage: "[player] [slaps] [tenths between]")]
    public void OnSlapCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => SlapCommand(player, arg));
    }

    [ConsoleCommand("css_fent", "Spin your own view, or a player's (admin), warmup only")]
    [CommandHelper(minArgs: 0, usage: "[player] [seconds]")]
    public void OnFentCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => FentCommand(player, arg));
    }

    [ConsoleCommand("css_drug", "Warp your own camera, or a player's (admin), warmup only")]
    [CommandHelper(minArgs: 0, usage: "[player] [seconds]")]
    public void OnDrugCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => DrugCommand(player, arg));
    }

    [ConsoleCommand("css_narcan", "Clear .fent and .drug off yourself, a player (admin) or everyone")]
    [CommandHelper(minArgs: 0, usage: "[player|all]")]
    public void OnNarcanCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => NarcanCommand(player, arg));
    }

    // --- aiming a toy -----------------------------------------------------------------------------

    // The argument shape, and the rules it is read by, are in ToyArgs - under test, because "is a lone
    // number a target or an amount" is exactly the kind of question that gets answered differently by
    // two call sites a month apart.
    private static bool MeansYourself(string needle) => ToyArgs.MeansYourself(needle);

    // Who the toy lands on, with the access rule that goes with it: anybody may point one of these at
    // themselves, and pointing one at somebody else is still the admin flag.
    //
    // Self-serve is the whole reason the room asked for it - .fent is funniest when the person who typed
    // it is the one spinning - and it opens nothing: the server has no kick path, so a toy that any
    // player could aim at any other player would be the first griefing lever on it.
    private CCSPlayerController? ToyTarget(CCSPlayerController? caller, string needle, string verb)
    {
        if (MeansYourself(needle))
        {
            // From rcon there is nobody to aim at. The console has to name somebody.
            if (caller == null || !caller.IsValid)
            {
                Tell(caller, $"Usage: {verb} <player> - the console has no \"me\".");
                return null;
            }
            return caller;
        }

        // A number is the roster number from .players and resolves to that player or to nobody at all.
        // It is never fallen back to a name match: an unambiguous handle that quietly guesses is worse
        // than one that fails.
        // TryParse rather than Parse: char.IsDigit is true of every digit in Unicode, and twenty digits
        // of one is not an int either. Both are "nobody here is that", not an exception.
        CCSPlayerController? target;
        if (needle.All(char.IsDigit))
        {
            target = int.TryParse(needle, out int number) ? PlayerByNumber(number) : null;
            if (target == null)
            {
                Tell(caller, $"Nobody here is {needle} - .players for the list.");
                return null;
            }
        }
        else
        {
            target = FindPlayer(caller, needle);
            if (target == null) return null;
        }

        if (caller != null && caller.IsValid && Id(target) == Id(caller)) return target;
        if (IsAdmin(caller)) return target;

        Tell(caller, $"You can only {verb} yourself - {verb} on its own, or \"{verb} me\".");
        return null;
    }

    // .kill is for the player who is stuck in a corner of a warmup map, not a way out of a lost round, and
    // it must not decide a captains' duel either. An admin may point it at whoever is being obnoxious in
    // the warmup, under the same fence as every other toy.
    private void KillCommand(CCSPlayerController? player, string arg)
    {
        string needle = ToyArgs.Split(arg).Needle;

        if (_phase != PugPhase.Idle)
        {
            Tell(player, "Not during a pug - wait for the teams.");
            return;
        }
        if (!InWarmupPeriod() || MatchRunning)
        {
            Tell(player, "Only during warmup.");
            return;
        }

        var target = ToyTarget(player, needle, ".kill");
        if (target == null) return;

        // Worked out after the target is resolved, not from the argument: an admin who typed their own
        // roster number has killed themselves, whatever they typed to say so.
        bool onSomebodyElse = player == null || !player.IsValid || Id(target) != Id(player);

        if (!target.PawnIsAlive)
        {
            Tell(player, onSomebodyElse ? $"{target.PlayerName} is already dead." : "You are already dead.");
            return;
        }

        target.CommitSuicide(explode: false, force: true);
        if (onSomebodyElse)
        {
            Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} was killed by an admin.");
        }
    }

    // The second argument is how many slaps, not how much damage: ".slap 4 5" is five slaps at player 4.
    // A slap does no damage at all now - it is a shove, and .kill is the one that kills.
    //
    // The third is the gap between them in tenths of a second, so ".slap 4 5 2" is five slaps 200ms
    // apart. Both are capped: fifteen slaps a second apart is fifteen seconds of somebody else's warmup,
    // which is already the far end of funny.
    private const int SlapsMax = 15;
    private const int SlapTenthsMax = 10;
    private const int SlapTenthsDefault = 4;

    private void SlapCommand(CCSPlayerController? caller, string arg)
    {
        if (!WarmupToy(caller, out _)) return;

        var (needle, amount, gap) = ToyArgs.Split(arg);
        var target = ToyTarget(caller, needle, ".slap");
        if (target == null) return;

        int slaps = Math.Clamp(amount ?? 1, 1, SlapsMax);
        int tenths = Math.Clamp(gap ?? SlapTenthsDefault, 1, SlapTenthsMax);
        if (!target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            Tell(caller, $"{target.PlayerName} is not alive.");
            return;
        }

        StartSlaps(Id(target), slaps, tenths);
        Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} got slapped"
            + (slaps > 1 ? $" {slaps} times." : "."));
    }

    // Spaced out rather than all at once: fifteen shoves in one frame is one big shove, and the joke is
    // the flurry. The first lands immediately so a single .slap is not a command with a delay on it.
    //
    // One timer per target, keyed by id, so a second .slap restarts the count instead of stacking two
    // timers that fight over the same pawn - the same reason .fent and .drug are keyed that way.
    private readonly Dictionary<ulong, CounterStrikeSharp.API.Modules.Timers.Timer> _slapTimers = new();

    private void StartSlaps(ulong id, int slaps, int tenths)
    {
        StopSlaps(id);

        var first = PlayerBySteamId(id);
        if (first != null && first.IsValid) Slap(first);
        if (slaps <= 1) return;

        int left = slaps - 1;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(tenths / 10.0f, () =>
        {
            var player = PlayerBySteamId(id);
            if (--left < 0 || player == null || !player.IsValid || !player.PawnIsAlive
                || _phase != PugPhase.Idle || !InWarmupPeriod())
            {
                // Killing this timer from inside its own tick is the one case CSSharp handles - see
                // StartFent for what killing a different one from in here does.
                timer?.Kill();
                _slapTimers.Remove(id);
                return;
            }
            Slap(player);
        }, TimerFlags.REPEAT);

        _slapTimers[id] = timer;
    }

    private void StopSlaps(ulong id)
    {
        if (!_slapTimers.Remove(id, out var running)) return;
        running.Kill();
    }

    // Velocity goes on through Teleport rather than by assigning to AbsVelocity: the field is networked
    // from the pawn and writing it in place is not replicated, so the player never actually moves.
    private void Slap(CCSPlayerController target)
    {
        var pawn = target.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var velocity = new Vector(
            pawn.AbsVelocity.X + _rng.Next(-240, 241),
            pawn.AbsVelocity.Y + _rng.Next(-240, 241),
            pawn.AbsVelocity.Z + 300);
        pawn.Teleport(null, null, velocity);
        SlapSound(pawn);
    }

    // .sound, an admin's probe for the one thing about a sound that cannot be read off a disk: whether the
    // engine will actually play it. A soundevent name that does not resolve is silence and no error, and
    // the names in the engine binary's string table are not all live soundevents, so the only way to tell
    // slap_sound from a typo is to fire it at a pawn and listen.
    //
    // The GUID EmitSound hands back is not the answer: every name below came back with one and only some
    // of them made a noise, so the log narrows nothing and the ears decide. Bare .sound walks the lot
    // 1.5s apart so one person can hear which is which.
    //
    // [SOURCE: measured on this server, CS2 PatchVersion 1.41.9.2, 2026-10-30 - Player.DamageKevlar is
    // audible, Player.DamageBody is not, both returned a non-zero guid.]
    private static readonly string[] SoundCandidates =
    [
        "Player.DamageBody",
        "Player.DamageKevlar",
        "Player.DamageHeadShot",
        "Player.DamageFall",
        "Player.Death",
        "Player.PickupWeapon",
        "Flesh.BulletImpact",
        "Weapon_Knife.Hit",
        "Weapon_Knife.HitWall",
        "UI.CounterBeep",
        "Buttons.snd9",
    ];

    private void SoundCommand(CCSPlayerController? caller, string arg)
    {
        if (!WarmupToy(caller, out _)) return;
        if (!IsAdmin(caller))
        {
            Tell(caller, "You do not have access to this command.");
            return;
        }
        if (caller == null || !caller.IsValid || !caller.PawnIsAlive)
        {
            Tell(caller, ".sound plays off your own pawn, so it needs one - be alive and in the room.");
            return;
        }

        var names = arg.Length > 0 ? [arg] : SoundCandidates;
        float at = 0;
        foreach (string name in names)
        {
            string sound = name;
            AddTimer(at, () => PlayProbeSound(caller, sound));
            at += 1.5f;
        }
    }

    private void PlayProbeSound(CCSPlayerController caller, string sound)
    {
        if (!caller.IsValid) return;
        var pawn = caller.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        uint guid;
        try
        {
            guid = pawn.EmitSound(sound);
        }
        catch (Exception exception)
        {
            Tell(caller, $"{sound}: threw - {exception.Message}");
            Logger.LogWarning("Sound probe \"{Sound}\" threw: {Message}", sound, exception.Message);
            return;
        }

        Tell(caller, $"{sound}: guid {guid}");
        Logger.LogInformation("Sound probe \"{Sound}\" -> guid {Guid}.", sound, guid);
    }

    // A slap you can hear. Velocity alone is somebody silently flying sideways, which reads as a bug
    // rather than a joke, and the flurry a .slap 10 makes is only funny if it ticks.
    //
    // CS2 has no slap sound, so it borrows a hit: the sound event is emitted from the pawn, so it is
    // positional and the room hears it where the slapped player is. Configurable because the name is the
    // one part of this that a CS2 update can take away - an empty string turns it off, and a wrong name
    // is silent rather than fatal, so the fallback is already the old behaviour.
    private bool _slapSoundComplained;

    private void SlapSound(CBasePlayerPawn pawn)
    {
        string sound = Config.SlapSound;
        if (sound.Length == 0) return;

        try
        {
            uint guid = pawn.EmitSound(sound);
            // Once a run, because the answer cannot change until the config does. The guid is not a
            // verdict - the engine hands one back for a name it then plays nothing for - so this is a
            // breadcrumb for the next person reading the log, and .sound is how you actually check.
            if (_slapSoundComplained) return;
            _slapSoundComplained = true;
            Logger.LogInformation("Slap sound \"{Sound}\" -> guid {Guid}.", sound, guid);
        }
        catch (Exception exception)
        {
            if (_slapSoundComplained) return;
            _slapSoundComplained = true;
            Logger.LogWarning("Slap sound \"{Sound}\" would not play: {Message}", sound, exception.Message);
        }
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

    // Shared by both: warmup, "[player] [seconds]", and the player has to be alive.
    private CCSPlayerController? EffectTarget(CCSPlayerController? caller, string arg, string verb, out int seconds)
    {
        seconds = 10;
        if (!WarmupToy(caller, out _)) return null;

        var (needle, amount, _) = ToyArgs.Split(arg);
        if (amount != null) seconds = Math.Clamp(amount.Value, 1, EffectMaxSeconds);

        var target = ToyTarget(caller, needle, verb);
        if (target == null) return null;
        if (!target.PawnIsAlive)
        {
            Tell(caller, $"{target.PlayerName} is not alive.");
            return null;
        }
        return target;
    }

    // .narcan takes it all back off again: the effect, the spun view and the warped camera. It exists
    // because .fent on yourself for thirty seconds is funny for about eight of them, and because a
    // player who has been handed a spinning view has no way to put it down.
    //
    // Yourself for free, somebody else on the admin flag, and "all" for the room - which is the one an
    // admin actually reaches for, because by then four people are spinning at once.
    private void NarcanCommand(CCSPlayerController? caller, string arg)
    {
        if (!WarmupToy(caller, out _)) return;

        string needle = ToyArgs.Split(arg).Needle;

        if (needle is "all" or "everyone" or "*")
        {
            if (!IsAdmin(caller))
            {
                Tell(caller, "Only an admin can .narcan the room - .narcan on its own sobers you up.");
                return;
            }
            int live = ClearFunEffects();
            Announce(live > 0
                ? $"Narcan all round - {live} effect(s) cleared."
                : "Narcan all round - nothing was running.");
            return;
        }

        var target = ToyTarget(caller, needle, ".narcan");
        if (target == null) return;

        Narcan(target);
        Announce($"{Colorize("{green}")}{target.PlayerName}{Colorize("{default}")} got narcan'd.");
    }

    // One player, all the way back to normal: both timers stopped, the view levelled, the camera and the
    // screen put back. StopFent and StopDrug each undo their own, and Sober runs over the top of them so
    // somebody who was hit by a toy the plugin has since forgotten about - a reconnect, a plugin reload
    // mid-trip - is still cleaned up.
    private void Narcan(CCSPlayerController player)
    {
        StopFent(Id(player));
        StopDrug(Id(player));
        StopSlaps(Id(player));
        Sober(player);
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
                // A spin that runs its time out has to level the view the same way one that is stopped
                // early does. Without this the common case - nobody touches it, the ten seconds elapse -
                // is the one that leaves somebody staring at the floor.
                LevelTheView(id);
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
        LevelTheView(id);
    }

    // Level them up again, keeping the yaw. The spin leaves the view wherever the last nudge put it,
    // which is usually staring at the sky or the floor, and in a match that is somebody's first round
    // ruined.
    private void LevelTheView(ulong id)
    {
        var player = PlayerBySteamId(id);
        var pawn = player?.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;
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

    // Everything a toy can leave behind on one player, put back: the camera, the screen and the view
    // angles. SoberUp is the camera and the screen, which is all .drug touches; .fent leaves the pawn's
    // transform wherever it stopped, so .narcan and the warmup edge go through here instead.
    private void Sober(CCSPlayerController player)
    {
        SoberUp(player);
        LevelTheView(Id(player));
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
    // Returns how many toys were live when it ran: the warmup edge logs a warning on anything above
    // zero, because by then a phase change should already have cleared them.
    internal int ClearFunEffects()
    {
        int live = ClearSmokePractice() + _fentTimers.Count + _drugTimers.Count + _slapTimers.Count;
        foreach (var id in _fentTimers.Keys.ToList()) StopFent(id);
        foreach (var id in _drugTimers.Keys.ToList()) StopDrug(id);
        foreach (var id in _slapTimers.Keys.ToList()) StopSlaps(id);

        // Timers only know the players they were started on. Anyone who reconnected, or who was drugged
        // before a plugin reload, is caught by sweeping the whole server.
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            if (player.DesiredFOV is not (0 or NormalFov)) Sober(player);
        }
        return live;
    }

    // Both toys are warmup-only: a slap or a swimming view during a live round decides it.
    private bool WarmupToy(CCSPlayerController? admin, out bool ok)
    {
        ok = _phase == PugPhase.Idle && InWarmupPeriod() && !MatchRunning;
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
