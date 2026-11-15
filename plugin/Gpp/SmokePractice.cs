using System.Drawing;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

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
// The trail is per-*client*, not per-grenade: a client with the cvars set draws the arc and the inset for
// every grenade it can see. There is no owner filter on the drawing, so the filter is on what the client
// gets: a practising player is not sent anybody else's grenade projectiles at all - smokes, flashes, HEs,
// molotovs, decoys - so there is no flight, trail or inset to switch to, and no cloud, since the cloud is
// the same entity.
//
// [SOURCE: `find grenade_traj` on this server, CS2 1.41.9.2, read 2026-10-24. The cvars' flags are the
//  documented part; that a per-client replication is enough to draw a trail for one player is read off the
//  behaviour, not a contract - re-check after a CS2 update.]
//
// One smoke in the air each: the next is handed over when the last one pops, not when it is thrown. The
// landing inset follows whichever grenade the client saw thrown last, so a second throw mid-flight takes
// the camera off the first.
//
// One popped smoke each: when a practising player's smoke pops, the last one they popped is removed. The cloud is the smokegrenade_projectile entity itself (the voxel
// field the client renders is networked from it), so removing the entity takes the cloud with it.
public partial class Gpp
{
    // Who has it on, by the same id everything else here keys players on.
    private readonly HashSet<ulong> _smokePractice = [];

    // The last smoke each of them popped: the entity index, and the thrower handle that was on it at the
    // time. Indices are reused, so the index alone cannot say whether the smoke sitting at it now is the
    // one we tracked - with several people practising it is as likely to be somebody else's. The handle
    // carries a serial number, which is what tells those two apart.
    private readonly Dictionary<ulong, (int Index, uint Thrower)> _poppedSmoke = [];

    // When each of them threw the smoke that has not popped yet, on the server clock.
    private readonly Dictionary<ulong, float> _smokeInFlight = [];

    // Longer than any smoke takes to come to rest and pop.
    private const float MaxSmokeFlight = 15f;

    private const string SmokeItem = "weapon_smokegrenade";
    private const string SmokeProjectile = "smokegrenade_projectile";

    private void HookSmokePractice()
    {
        // Every live grenade projectile, so the transmit hook can hide the ones a practising viewer did not
        // throw - any type, because the inset follows whatever grenade the client last saw thrown. Every
        // "*_projectile" in CS2 is a grenade. Keyed by the native pointer, which is what OnEntityDeleted
        // hands back.
        RegisterListener<Listeners.OnEntitySpawned>(entity =>
        {
            if (!entity.DesignerName.EndsWith("_projectile", StringComparison.Ordinal)) return;
            _liveGrenades[entity.Handle] = new CBaseGrenade(entity.Handle);
        });
        RegisterListener<Listeners.OnEntityDeleted>(entity => _liveGrenades.Remove(entity.Handle));

        RegisterEventHandler<EventGrenadeThrown>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;
            if (!@event.Weapon.Contains("smoke", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

            ulong who = Id(player);
            if (!_smokePractice.Contains(who)) return HookResult.Continue;

            // One line per practice throw, in GPP's own log: it is the only way to see from off the server
            // whether grenade_thrown fires at all and what it calls the weapon.
            Logger.LogInformation("smokes: {Player} threw {Weapon}", player.PlayerName, @event.Weapon);

            // Where they were standing and looking as it left their hand: the numbers nobody can read off
            // their own screen, and the half of a lineup that pixels cannot give you.
            var pawn = player.PlayerPawn.Value;
            string? line = pawn?.AbsOrigin == null
                ? null
                : Smokes.SmokeThrowLine(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z,
                    pawn.V_angle.X, pawn.V_angle.Y);

            _smokeInFlight[who] = Server.CurrentTime;

            AfterTick(() =>
            {
                var them = PlayerBySteamId(who);
                if (them == null || !them.IsValid) return;
                if (line != null) them.PrintToChat(Prefix() + line);
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

            _smokeInFlight.Remove(who);

            AfterTick(() =>
            {
                CullPoppedSmoke(who, keep: popped);
                // Read off the new cloud rather than off the player: by the time this one is culled they
                // may be on a different pawn, and the handle that threw it will not be.
                var fresh = Utilities.GetEntityFromIndex<CSmokeGrenadeProjectile>(popped);
                _poppedSmoke[who] = (popped, fresh != null && fresh.IsValid ? fresh.Thrower.Raw : 0u);

                MarkPop(who, x, y, z);

                var them = PlayerBySteamId(who);
                if (them == null || !them.IsValid) return;
                them.PrintToChat(Prefix() + Smokes.SmokePoppedLine(x, y, z));
                ReArmSmoke(them);
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
                MakeInvulnerable(them);
                StripToSmokes(them);
                ReArmSmoke(them);
            });
            return HookResult.Continue;
        });

        // What keeps the lock on. Warmup money is unlimited, so the buy menu is one click away from an
        // AWP all evening - a purchase or a weapon off the floor is taken back the moment it lands.
        RegisterEventHandler<EventItemPurchase>((@event, _) => KeepPracticeKit(@event.Userid, bought: true));
        RegisterEventHandler<EventItemPickup>((@event, _) => KeepPracticeKit(@event.Userid, bought: false));

        // A cloud that has timed out on its own is nothing left to cull, and the index it held is about to
        // belong to something else.
        RegisterEventHandler<EventSmokegrenadeExpired>((@event, _) =>
        {
            var player = @event.Userid;
            if (Retired || player == null || !player.IsValid) return HookResult.Continue;

            ulong who = Id(player);
            if (_poppedSmoke.TryGetValue(who, out var tracked) && tracked.Index == @event.Entityid)
            {
                _poppedSmoke.Remove(who);
            }
            return HookResult.Continue;
        });

        // A warmup restart takes every projectile with it and fires no expired event for any of them, so
        // every tracked index is stale from here on - and a stale index is how this removes a cloud that
        // belongs to somebody else.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _poppedSmoke.Clear();
            _smokeInFlight.Clear();
            ClearPopMarkers();
            return HookResult.Continue;
        });
    }

    // Called from the warmup tick, once a second. The pop re-arms on the next frame when it fires; this is
    // what makes an empty hand impossible whether it fires or not - a practising player with nothing to
    // throw once their smoke is down is the feature not working, and it is a cheap thing to assert.
    internal void TopUpPracticeSmokes()
    {
        if (_smokePractice.Count == 0) return;
        if (_phase != PugPhase.Idle || !InWarmupPeriod()) return;

        foreach (ulong who in _smokePractice.ToList())
        {
            var player = PlayerBySteamId(who);
            if (player == null || !player.IsValid) continue;
            ReArmSmoke(player);
        }
    }

    // Both of the ways a practising player ends up holding something else. The weapon is already theirs by
    // the time either event fires, so this takes it back on the next frame rather than trying to refuse it.
    private HookResult KeepPracticeKit(CCSPlayerController? player, bool bought)
    {
        if (Retired || player == null || !player.IsValid) return HookResult.Continue;

        ulong who = Id(player);
        if (!_smokePractice.Contains(who)) return HookResult.Continue;

        AfterTick(() =>
        {
            var them = PlayerBySteamId(who);
            if (them == null || !them.IsValid || !_smokePractice.Contains(who)) return;

            StripToSmokes(them);
            // Only on a purchase: a pickup is often the smoke we just handed them, and a line of chat per
            // weapon touched would be its own kind of spam.
            if (bought)
            {
                them.PrintToChat(Prefix() + $"Smokes only while practice is on - "
                    + $"{Colorize("{lime}")}.smokes off{Colorize("{default}")} to buy.");
            }
        });
        return HookResult.Continue;
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
            // The tracked index goes with it: left behind it outlives its cloud, and the engine hands that
            // index to the next person's smoke.
            ForgetSmokePractice(who);
            ShowSmokeTrails(player, 0);
            TakeDamageAgain(player);
            player.PrintToChat(Prefix() + "Smoke practice off. Buy what you like again - warmup money is "
                + "back to being unlimited.");
            return;
        }

        _smokePractice.Add(who);
        SyncMarkerTransmit();
        ShowSmokeTrails(player, Config.SmokeTrailSeconds);
        MakeInvulnerable(player);
        StripToSmokes(player);
        ReArmSmoke(player);
        player.PrintToChat(Prefix() + $"Smoke practice on: {Config.SmokeTrailSeconds}s trail, smokes and "
            + "nothing else, and nothing can hurt you. One in the air at a time; only your last popped smoke is kept.");
        player.PrintToChat(Prefix() + $"{Colorize("{lime}")}.smokes off{Colorize("{default}")} when you are done.");
    }

    // The two cvars, to one client. Off writes the values back rather than leaving the client holding a
    // trail it would carry into a live round.
    // Seconds of trail, or 0 for off.
    private static void ShowSmokeTrails(CCSPlayerController player, int seconds)
    {
        player.ReplicateConVar("sv_grenade_trajectory_prac_trailtime", seconds.ToString());
        player.ReplicateConVar("sv_grenade_trajectory_prac_pipreview", seconds > 0 ? "true" : "false");
    }

    // Invulnerable while they are practising: a lineup takes a few goes, and somebody else warming up in
    // the same room should not be able to end one. Damage is worked out server-side, so clearing the flag
    // is enough - nothing has to be networked to the client for it to stop taking hits.
    private static void MakeInvulnerable(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.TakesDamage = false;
    }

    // And mortal again. Everything that ends practice comes through here: an invulnerable player left in a
    // live match is the one failure of this feature nobody would notice until it decided a round.
    private static void TakeDamageAgain(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.TakesDamage = true;
    }

    // Smokes and a knife, nothing else. The point of the toggle is to look at smokes, and warmup money is
    // unlimited - without this the first thing anybody does is buy an AWP and practice turns into a
    // deathmatch with trails on.
    private void StripToSmokes(CCSPlayerController player)
    {
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons != null)
        {
            // Names first, entities second: removing an item out of the list being walked is how this
            // reads an entity that has just gone.
            var unwanted = new List<string>();
            foreach (var handle in weapons)
            {
                string? name = handle.Value?.DesignerName;
                if (name == null || IsPracticeKit(name)) continue;
                if (!unwanted.Contains(name)) unwanted.Add(name);
            }
            foreach (var name in unwanted) Utilities.RemoveItemByDesignerName(player, name, true);
        }
    }

    // A knife stays: it is the default melee, and it is also what everybody moves at full speed with.
    // Skinned knives come through with their own designer name, hence the match rather than a list.
    private static bool IsPracticeKit(string designerName) =>
        designerName == SmokeItem
        || designerName.Contains("knife", StringComparison.OrdinalIgnoreCase)
        || designerName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);

    // A smoke back in hand, and only when they have none and none is in the air. A give fires
    // item_pickup, which is one of the two hooks holding the loadout lock - without the HoldsSmoke check
    // that hook hands them another smoke, which fires another pickup, for as long as the server is up.
    private void ReArmSmoke(CCSPlayerController player)
    {
        if (!player.PawnIsAlive || HoldsSmoke(player)) return;
        ulong who = Id(player);
        float? thrownAt = _smokeInFlight.TryGetValue(who, out float t) ? t : null;
        if (!Smokes.MayReArm(thrownAt, Server.CurrentTime, MaxSmokeFlight)) return;
        _smokeInFlight.Remove(who);
        player.GiveNamedItem(SmokeItem);
    }

    private static bool HoldsSmoke(CCSPlayerController player)
    {
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var handle in weapons)
        {
            if (handle.Value?.DesignerName == SmokeItem) return true;
        }
        return false;
    }

    // Remove the cloud this player popped before the one that has just landed.
    private void CullPoppedSmoke(ulong who, int keep)
    {
        if (!_poppedSmoke.TryGetValue(who, out var last)) return;
        _poppedSmoke.Remove(who);
        if (last.Index == keep || last.Thrower == 0) return;

        var smoke = Utilities.GetEntityFromIndex<CSmokeGrenadeProjectile>(last.Index);
        // An index is reused the moment the engine wants it back, so a stale one can name an entity that
        // is alive and is not a smoke at all. All three checks are load-bearing, and the last is the one
        // that keeps this from removing a cloud somebody else is looking at.
        if (smoke == null || !smoke.IsValid || smoke.DesignerName != SmokeProjectile) return;
        if (smoke.Thrower.Raw != last.Thrower) return;
        smoke.Remove();
    }

    // Nothing here may survive into a pug, and a client left holding a trail would draw one in a live
    // round. Called from ClearFunEffects (vote, draft, handoff, map change), on the warmup edge, and on
    // unload - a hot reload is how this plugin is deployed, and the new instance knows nothing of who had
    // it on.
    // Returns how many players were actually practising, so the caller can say something when the answer
    // should have been none.
    internal int ClearSmokePractice()
    {
        int were = _smokePractice.Count(who => PlayerBySteamId(who) != null);
        var practising = _smokePractice.ToList();
        _smokePractice.Clear();
        _poppedSmoke.Clear();
        _smokeInFlight.Clear();
        ClearPopMarkers();

        // The cvars and the damage flag go back for everybody, not only the names this instance knows:
        // a reload loses the set, and an invulnerable player nobody is tracking is the one failure here
        // that would decide a round.
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            ShowSmokeTrails(player, 0);
            TakeDamageAgain(player);
        }

        // Only the people who had it on are told, and only once.
        foreach (ulong who in practising)
        {
            var player = PlayerBySteamId(who);
            if (player != null && player.IsValid) player.PrintToChat(Prefix() + "Smoke practice off.");
        }
        SyncMarkerTransmit();
        return were;
    }

    private void ForgetSmokePractice(ulong who)
    {
        _smokePractice.Remove(who);
        _poppedSmoke.Remove(who);
        _smokeInFlight.Remove(who);
        RemovePopMarker(who);
        SyncMarkerTransmit();
    }

    // A pillar on the pop, for the thrower alone, for SmokeMarkerSeconds. One each, like the cloud: a new
    // pop moves it. The beam is visible to nobody else because the transmit hook drops it from every
    // other client's snapshot - attached only while a marker stands or somebody is practising, since it
    // runs per client per tick.
    private readonly Dictionary<ulong, CBeam> _popMarkers = [];
    private Listeners.CheckTransmit? _markerTransmit;
    private readonly Dictionary<nint, CBaseGrenade> _liveGrenades = [];

    private const float MarkerHeight = 160f;

    private void MarkPop(ulong who, float x, float y, float z)
    {
        RemovePopMarker(who);
        if (Config.SmokeMarkerSeconds <= 0) return;

        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        // A dead factory hands back a wrapper over a zero handle rather than null - see SpawnBoard.
        if (beam == null || beam.Handle == IntPtr.Zero || !beam.IsValid)
        {
            Logger.LogError("beam would not spawn - no smoke pop marker.");
            return;
        }

        beam.Render = Color.FromArgb(255, 0, 255, 0);
        beam.Width = 3f;
        beam.EndWidth = 3f;
        beam.Teleport(new Vector(x, y, z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
        beam.EndPos.X = x;
        beam.EndPos.Y = y;
        beam.EndPos.Z = z + MarkerHeight;
        beam.DispatchSpawn();

        _popMarkers[who] = beam;
        SyncMarkerTransmit();

        AddTimer(Config.SmokeMarkerSeconds, () =>
        {
            // Only the beam this timer was for: a later pop has its own timer.
            if (_popMarkers.TryGetValue(who, out var current) && ReferenceEquals(current, beam)) RemovePopMarker(who);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RemovePopMarker(ulong who)
    {
        if (!_popMarkers.Remove(who, out var beam)) return;
        if (beam.IsValid) beam.Remove();
        SyncMarkerTransmit();
    }

    private void ClearPopMarkers()
    {
        foreach (ulong who in _popMarkers.Keys.ToList()) RemovePopMarker(who);
    }

    private void SyncMarkerTransmit()
    {
        bool wanted = !Retired && (_popMarkers.Count > 0 || _smokePractice.Count > 0);
        if (wanted == (_markerTransmit != null)) return;

        if (wanted)
        {
            _markerTransmit = PopMarkerTransmit;
            RegisterListener(_markerTransmit);
            return;
        }
        RemoveListener(_markerTransmit!);
        _markerTransmit = null;
    }

    private void PopMarkerTransmit(CCheckTransmitInfoList infos)
    {
        foreach (var (info, receiver) in infos)
        {
            if (receiver == null) continue;
            ulong viewer = Id(receiver);
            foreach (var (owner, beam) in _popMarkers)
            {
                if (owner != viewer && beam.IsValid) info.TransmitEntities.Remove(beam);
            }

            // Somebody else's grenade, to somebody practising: not sent. Thrower is the pawn handle, and
            // a grenade whose thrower is gone is nobody's to keep.
            if (!_smokePractice.Contains(viewer)) continue;
            uint mine = receiver.PlayerPawn.Raw;
            foreach (var grenade in _liveGrenades.Values)
            {
                if (grenade.Thrower.Raw != mine) info.TransmitEntities.Remove(grenade);
            }
        }
    }

    [ConsoleCommand("css_smokes", "Smoke practice trails for yourself (warmup only)")]
    [CommandHelper(minArgs: 0, usage: "[on|off]")]
    public void OnSmokesCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgByIndex(1).ToLowerInvariant() : "";
        AfterTick(() => SmokesCommand(player, arg));
    }
}
