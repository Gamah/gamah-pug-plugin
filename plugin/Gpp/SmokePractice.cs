using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Gpp;

// .smokes - smoke practice during warmup, for one player at a time.
//
// The trail is the engine's own: sv_grenade_trajectory_prac_trailtime draws a thrown grenade's flight for
// that many seconds and sv_grenade_trajectory_prac_pipreview puts the landing spot in an inset. Both are
// FCVAR_REPLICATED and not cheats, which is what makes this per-player: the client draws from its own copy
// of the value, and CCSPlayerController.ReplicateConVar pushes a value to one client's copy. Nobody else's
// screen changes, and sv_cheats stays off - the cheat-flagged practice helpers (sv_rethrow_last_grenade,
// sv_kill_smokegrenade) would have meant turning cheats on for the whole public server, which hands every
// connected client the client-side ones as well.
// [SOURCE: `find grenade_traj` on this server, CS2 1.41.9.2, read 2026-10-24. The cvars' flags are the
//  documented part; that a per-client replication is enough to draw a trail for one player is read off the
//  behaviour, not a contract - re-check after a CS2 update.]
//
// One popped smoke each: when a practising player's smoke pops, the last one they popped is removed. The
// new grenade may fly while they are still looking at the previous cloud, which is the point - the cull
// lands at detonation, not at the throw. The cloud is the smokegrenade_projectile entity itself (the voxel
// field the client renders is networked from it), so removing the entity takes the cloud with it.
public partial class Gpp
{
    // Who has it on, by the same id everything else here keys players on.
    private readonly HashSet<ulong> _smokePractice = [];

    // The last smoke each of them popped, by entity index. Indices are reused, so nothing is trusted
    // without checking the entity is still alive and still a smoke.
    private readonly Dictionary<ulong, int> _poppedSmoke = [];

    private const string SmokeItem = "weapon_smokegrenade";
    private const string SmokeProjectile = "smokegrenade_projectile";

    private void HookSmokePractice()
    {
        RegisterEventHandler<EventGrenadeThrown>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;
            if (!@event.Weapon.Contains("smoke", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

            ulong who = Id(player);
            if (!_smokePractice.Contains(who)) return HookResult.Continue;

            // Where they were standing and looking as it left their hand: the numbers nobody can read off
            // their own screen, and the half of a lineup that pixels cannot give you.
            var pawn = player.PlayerPawn.Value;
            string? line = pawn?.AbsOrigin == null
                ? null
                : Smokes.SmokeThrowLine(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z,
                    pawn.V_angle.X, pawn.V_angle.Y);

            AfterTick(() =>
            {
                var them = PlayerBySteamId(who);
                if (them == null || !them.IsValid) return;
                if (line != null) them.PrintToChat(Prefix() + line);
                ReArmSmoke(them);
            });
            return HookResult.Continue;
        });

        RegisterEventHandler<EventSmokegrenadeDetonate>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;

            ulong who = Id(player);
            if (!_smokePractice.Contains(who)) return HookResult.Continue;

            int popped = @event.Entityid;
            float x = @event.X, y = @event.Y, z = @event.Z;

            AfterTick(() =>
            {
                CullPoppedSmoke(who, keep: popped);
                _poppedSmoke[who] = popped;

                var them = PlayerBySteamId(who);
                if (them != null && them.IsValid) them.PrintToChat(Prefix() + Smokes.SmokePoppedLine(x, y, z));
            });
            return HookResult.Continue;
        });

        // Re-asserted on spawn. A replicated value lives on the client, and nothing here can see whether a
        // round restart or a warmup restart has handed it the server's value back - the failure if it does
        // is a trail that silently stops, so it is written again every time they come back alive.
        RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;

            ulong who = Id(player);
            if (!_smokePractice.Contains(who)) return HookResult.Continue;

            AfterTick(() =>
            {
                var them = PlayerBySteamId(who);
                if (them == null || !them.IsValid) return;
                if (_phase != PugPhase.Idle || !InWarmupPeriod()) return;
                ShowSmokeTrails(them, Config.SmokeTrailSeconds);
            });
            return HookResult.Continue;
        });

        // A cloud that has timed out on its own is nothing left to cull, and the index it held is about to
        // belong to something else.
        RegisterEventHandler<EventSmokegrenadeExpired>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;

            ulong who = Id(player);
            if (_poppedSmoke.TryGetValue(who, out int tracked) && tracked == @event.Entityid)
            {
                _poppedSmoke.Remove(who);
            }
            return HookResult.Continue;
        });
    }

    // Everything the dot command and the console command share.
    private void SmokesCommand(CCSPlayerController? player, string arg)
    {
        if (player == null || !player.IsValid || player.IsBot) return;

        if (Config.SmokeTrailSeconds <= 0)
        {
            player.PrintToChat(Prefix() + "Smoke practice is turned off on this server.");
            return;
        }
        if (_phase != PugPhase.Idle || !InWarmupPeriod())
        {
            player.PrintToChat(Prefix() + "Only during warmup, and not while a pug is running.");
            return;
        }

        ulong who = Id(player);
        bool on = Smokes.SmokesWanted(arg, alreadyOn: _smokePractice.Contains(who));

        if (!on)
        {
            _smokePractice.Remove(who);
            ShowSmokeTrails(player, 0);
            player.PrintToChat(Prefix() + "Smoke practice off.");
            return;
        }

        _smokePractice.Add(who);
        ShowSmokeTrails(player, Config.SmokeTrailSeconds);
        player.PrintToChat(Prefix() + $"Smoke practice on: {Config.SmokeTrailSeconds}s trail, and your "
            + "throws come back. Only your last popped smoke is kept.");
        player.PrintToChat(Prefix() + $"{Colorize("{lime}")}.smokes off{Colorize("{default}")} when you are done.");

        // Something to throw, so the first one does not need the buy menu.
        ReArmSmoke(player);
    }

    // The two cvars, to one client. Off writes the values back rather than leaving the client holding a
    // trail it would carry into a live round.
    // Seconds of trail, or 0 for off.
    private static void ShowSmokeTrails(CCSPlayerController player, int seconds)
    {
        player.ReplicateConVar("sv_grenade_trajectory_prac_trailtime", seconds.ToString());
        player.ReplicateConVar("sv_grenade_trajectory_prac_pipreview", seconds > 0 ? "true" : "false");
    }

    // A smoke back in hand. ammo_grenade_limit_total caps how many anyone may hold, so this is a top-up
    // for the one they just threw rather than a stack of them.
    private static void ReArmSmoke(CCSPlayerController player)
    {
        if (!player.PawnIsAlive) return;
        player.GiveNamedItem(SmokeItem);
    }

    // Remove the cloud this player popped before the one that has just landed.
    private void CullPoppedSmoke(ulong who, int keep)
    {
        if (!_poppedSmoke.TryGetValue(who, out int index)) return;
        _poppedSmoke.Remove(who);
        if (index == keep) return;

        var smoke = Utilities.GetEntityFromIndex<CSmokeGrenadeProjectile>(index);
        // An index is reused the moment the engine wants it back, so a stale one can name an entity that
        // is alive and is not a smoke at all. Both checks are load-bearing.
        if (smoke == null || !smoke.IsValid || smoke.DesignerName != SmokeProjectile) return;
        smoke.Remove();
    }

    // Nothing here may survive into a pug, and a client left holding a trail would draw one in a live
    // round. Called from ClearFunEffects (vote, draft, handoff, map change), on the warmup edge, and on
    // unload - a hot reload is how this plugin is deployed, and the new instance knows nothing of who had
    // it on.
    internal void ClearSmokePractice()
    {
        _smokePractice.Clear();
        _poppedSmoke.Clear();

        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            ShowSmokeTrails(player, 0);
        }
    }

    private void ForgetSmokePractice(ulong who)
    {
        _smokePractice.Remove(who);
        _poppedSmoke.Remove(who);
    }

    [ConsoleCommand("css_smokes", "Smoke practice trails for yourself (warmup only)")]
    [CommandHelper(minArgs: 0, usage: "[on|off]")]
    public void OnSmokesCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgByIndex(1).ToLowerInvariant() : "";
        AfterTick(() => SmokesCommand(player, arg));
    }
}
