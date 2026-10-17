using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

public enum PugPhase
{
    Idle,
    MapVote,
    CaptainKnife,
    Draft,
}

public partial class Gpp : BasePlugin, IPluginConfig<GppConfig>
{
    public override string ModuleName => "Gamah's Pug Plugin";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleDescription => "Map vote, random captains and a captain draft, handed to MatchZy.";

    public GppConfig Config { get; set; } = new();

    private readonly Random _rng = new();
    private PugPhase _phase = PugPhase.Idle;

    // Set when a map vote has changed the map and the draft is waiting for the new map to come up.
    private bool _draftPendingMapChange;

    public void OnConfigParsed(GppConfig config)
    {
        Config = config;
        if (Config.HudLineBudget > 0) HudMetrics.LineBudget = Config.HudLineBudget;
        if (Config.PlayersPerTeam < 0) Config.PlayersPerTeam = 0;
        if (Config.MinPlayers < 2) Config.MinPlayers = 2;
        Config.MapPool = Config.MapPool.Where(m => !string.IsNullOrWhiteSpace(m.Name) || m.IsWorkshop).ToList();
        if (Config.MapPool.Count == 0)
        {
            Logger.LogWarning("map_pool is empty: !pug vote has nothing to offer and draft_then_veto cannot build a veto pool.");
        }
    }

    public override void Load(bool hotReload)
    {
        NeverKickAnyone();
        KeepMatchZyAsleep();

        HookMatchZy();
        HookJoins();
        HookReadyGate();
        HookCaptainKnife();
        HookFunCommands();
        StartWarmupMoney();
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
    }

    // Nobody is ever kicked off this server, match or no match. Both of these are MatchZy kicking people
    // on connect - the whitelist for anyone outside a loaded roster, the other for everyone at all when no
    // match is loaded - and both are cvars that outlive whatever set them, so they are asserted at load,
    // on every map, and in every match config the draft writes.
    private void NeverKickAnyone()
    {
        Server.ExecuteCommand("matchzy_whitelist_enabled_default false;matchzy_kick_when_no_match_loaded false");
    }

    // How many bots a test run wants on the server, and the timer that keeps putting them back.
    private int _botTarget;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _botTopUpTimer;

    // Topping up once does not hold. MatchZy execs MatchZy/warmup.cfg on every map start and every time it
    // restarts warmup, and that file's first line is `bot_quota 0`, so the bots are wiped again a second or
    // two later - the first version of this stopped as soon as it saw them and the draft still got an empty
    // server. The top-up therefore keeps running for as long as a test wants bots, repairing the quota
    // whenever MatchZy clears it, and only !pug bots 0 turns it off.
    internal void KeepTestBotsTopped(int target)
    {
        _botTarget = Math.Max(0, target);
        _botTopUpTimer?.Kill();
        _botTopUpTimer = null;

        if (_botTarget == 0)
        {
            Server.ExecuteCommand("bot_quota 0;bot_join_after_player 1;bot_kick");
            Logger.LogInformation("count_bots: bots turned off.");
            return;
        }

        _botTopUpTimer = AddTimer(2.0f, TopUpTestBots, TimerFlags.REPEAT);
        TopUpTestBots();
    }

    private void TopUpTestBots()
    {
        if (!Config.CountBots || _botTarget <= 0)
        {
            _botTopUpTimer?.Kill();
            _botTopUpTimer = null;
            return;
        }

        // The quota is asserted every time, not only when we are short: it is what trims a surplus left by
        // an earlier test, and what MatchZy keeps setting back to zero. bot_add is only for the shortfall,
        // and the quota caps whatever it does.
        Server.ExecuteCommand($"bot_join_after_player 0;bot_quota_mode normal;bot_quota {_botTarget}");

        int here = HumanPlayers().Count(player => player.IsBot);
        for (int i = here; i < _botTarget; i++) Server.ExecuteCommand("bot_add");
    }

    // MatchZy's ready phase belongs to a match the draft has handed over, not to an empty server. Left on
    // autostart, MatchZy calls StartWarmup() on the first connect and on every map start, which arms the
    // repeating "N players are not ready" chat timer and turns .ready on - during the vote and the draft,
    // before there are any teams to be ready for. Sleep mode (autostart_mode 0) leaves readyAvailable
    // false, so the timer is never armed and .ready falls through silently, while matchzy_loadmatch still
    // wakes it at the handoff. [SOURCE: MatchZy 0.9.1 Utility.cs SendUnreadyPlayersMessage/AutoStart,
    // SleepMode.cs StartSleepMode, read 2026-10-15]
    //
    // Sleep mode execs cfg/MatchZy/sleep.cfg, and that file - not this plugin - owns holding the warmup
    // open afterwards; stock it ends the warmup instead, which leaves an idle server playing live rounds.
    private static void KeepMatchZyAsleep()
    {
        Server.ExecuteCommand("matchzy_autostart_mode 0");
    }

    private void OnMapStart(string mapName)
    {
        NeverKickAnyone();
        KeepMatchZyAsleep();
        ClearFunEffects();

        if (!_draftPendingMapChange) return;
        _draftPendingMapChange = false;
        RestoreTestBots();
        // Players reconnect at their own pace after a map change. Drafting before they are back sized the
        // teams from whoever happened to be in first, so wait for the headcount from before the change.
        AddTimer(5.0f, StartWaitingForPlayers);
    }

    private void OnClientDisconnect(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid) return;
        var steamId = Id(player);

        // Next frame: this runs before the player is actually gone, so liveness checks still see them.
        Server.NextFrame(() =>
        {
            if (_phase == PugPhase.Draft) HandleDraftDisconnect(steamId);
            else if (_phase == PugPhase.MapVote) _votes.Remove(steamId);
        });
    }

    [ConsoleCommand("css_pug", "GPP admin command: start | vote | draft | captains | stop | cancel | clear | status | flow | bots <n> | hudsize | hudboth | hudlist [abcd]")]
    [CommandHelper(minArgs: 0, usage: "<start|vote|draft|captains|stop|cancel|clear|status|flow|hudsize|hudlist>")]
    public void OnPugCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsAdmin(player))
        {
            Reply(command, "You do not have access to this command.");
            return;
        }

        string sub = command.ArgCount > 1 ? command.ArgByIndex(1).ToLowerInvariant() : "status";
        string arg = command.ArgCount > 2 ? command.ArgByIndex(2).ToLowerInvariant() : "";

        bool force = false;
        int size = 0;
        for (int i = 2; i < command.ArgCount; i++)
        {
            string value = command.ArgByIndex(i).ToLowerInvariant();
            if (value == "force") force = true;
            else if (int.TryParse(value, out int parsed) && parsed > 0) size = parsed;
        }

        switch (sub)
        {
            case "start":
                _teamSizeOverride = size;
                AfterTick(() =>
                {
                    if (Config.Flow == "draft_then_veto") StartDraft(force);
                    else StartMapVote(force);
                });
                break;
            case "vote":
                _teamSizeOverride = size;
                AfterTick(() => StartMapVote(force));
                break;
            case "draft":
                _teamSizeOverride = size;
                AfterTick(() => StartDraft(force));
                break;
            case "captains":
                RerollCaptains(command);
                break;
            case "clear":
                _matchLoaded = false;
                StopReadyGate();
                Reply(command, "Forgot the loaded MatchZy match (nothing was ended).");
                break;
            case "cancel":
                AfterTick(() => Cancel("An admin cancelled it."));
                break;
            case "stop":
            case "reset":
                AfterTick(() => StopEverything("An admin stopped it."));
                break;
            case "flow":
                if (arg is "vote_then_draft" or "draft_then_veto")
                {
                    Config.Flow = arg;
                    Reply(command, $"Flow set to {arg} (for this session; edit the config to make it stick).");
                }
                else
                {
                    Reply(command, "Usage: !pug flow <vote_then_draft|draft_then_veto>");
                }
                break;
            case "bots":
                if (!Config.CountBots)
                {
                    Reply(command, "count_bots is off in Gpp.json - bots would not be counted as players.");
                    break;
                }
                KeepTestBotsTopped(size);
                Reply(command, size > 0
                    ? $"Keeping {size} bots on the server for a test run."
                    : "Bots off.");
                break;
            case "preview":
                if (arg == "stop") { StopPreview(); StopHud(); Reply(command, "Preview stopped."); }
                else HudPreview();
                break;
            case "ruler":
                HudRulerProbe(arg, size);
                break;
            case "hudsize":
                HudSizeProbe();
                break;
            case "hudboth":
                HudBothProbe();
                break;
            case "hudlist":
                HudListProbe(arg);
                break;
            case "status":
            default:
                Reply(command, Status());
                break;
        }
    }

    private string Status()
    {
        return _phase switch
        {
            PugPhase.MapVote => $"Map vote running, {_votes.Count} vote(s) in, {_voteSecondsLeft}s left.",
            PugPhase.CaptainKnife => $"Captains knifing for first pick, {_knifeSecondsLeft}s left.",
            PugPhase.Draft => DraftStatus(),
            _ => $"Idle. Flow: {Config.Flow}. {HumanPlayers().Count} player(s) connected. "
                 + (_matchLoaded ? "A MatchZy match is loaded." : "No MatchZy match loaded."),
        };
    }

    private void Cancel(string why)
    {
        if (_phase == PugPhase.Idle)
        {
            Announce("Nothing to cancel.");
            return;
        }
        string what = _phase switch
        {
            PugPhase.MapVote => "Map vote",
            PugPhase.CaptainKnife => "Knife for first pick",
            _ => "Draft",
        };
        ResetVote();
        ResetKnife();
        ResetDraft();
        StopReadyHud();
        ReleaseHeldSpectators();
        ReleaseTheRound();
        _waitTimer?.Kill();
        _waitTimer = null;
        _votedMap = null;
        _draftPendingMapChange = false;
        _phase = PugPhase.Idle;
        Announce($"{what} cancelled. {why}");
    }

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return true; // server console / rcon
        return AdminManager.PlayerHasPermissions(player, Config.AdminFlag);
    }

    // Connected players, excluding GOTV. count_bots pulls bots in for a solo test run.
    private List<CCSPlayerController> HumanPlayers()
    {
        return Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsHLTV && (!p.IsBot || Config.CountBots))
            .ToList();
    }

    // Everything here keys players by SteamID, and a bot's is 0 - so every bot would be the same player.
    // Bots get an id from their slot instead, which is stable for as long as they are connected and is all
    // a test run needs. Nothing outside this file needs to know which kind it is holding.
    //
    // The two ranges must not overlap, and the first attempt at this put the bot range *above* the SteamID
    // range: every real player then read as a bot, which emptied the MatchZy rosters and handed humans the
    // three-second pick clock meant for bots. Every SteamID64 is at or above 76561197960265728, so bots go
    // at the bottom instead, where nothing real can reach.
    private const ulong SteamId64Floor = 76561197960265728;
    private const ulong BotIdBase = 1;

    private static ulong Id(CCSPlayerController player)
    {
        return player.IsBot ? BotIdBase + (ulong)player.Slot : player.SteamID;
    }

    private static bool IsBotId(ulong id) => id > 0 && id < SteamId64Floor;

    private CCSPlayerController? PlayerBySteamId(ulong id)
    {
        return HumanPlayers().FirstOrDefault(p => Id(p) == id);
    }

    private string Prefix() => Colorize(Config.ChatPrefix) + " ";

    private const string Everybody = "EVERYBODY";

    // One line per thing that happened, ending in what to type and who types it. The overlay carries the
    // lists and the countdowns, so chat does not repeat them.
    private void Prompt(string message, string command, string who)
    {
        Announce($"{message}  {Colorize("{lime}")}{command}{Colorize("{default}")} "
            + $"{Colorize("{gold}")}({who}){Colorize("{default}")}");
    }

    // Warmup's own centre-screen text flashes over the overlay, and with everyone in spectator a running
    // round would tick to its own end, so the pug phases hold the round still instead.
    private void HoldTheRound()
    {
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1;mp_warmup_end");
    }

    private void ReleaseTheRound()
    {
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_warmup_start");
    }

    // force_spectate cannot put a bot out of the way: a bot in spectator is outside bot_quota, so the
    // engine kicks it and adds a replacement and the pug referees a churn of new bots instead of running.
    // bot_stop and bot_zombie look like the answer and are not - both are cheat-protected, so with
    // sv_cheats 0 they take the value, report it back, and change nothing; the first version of this
    // "froze" ten bots that then knife-fought their way through a captains' duel.
    //
    // Killing them is what actually works without cheats. The duel's own cvars turn respawning off, and
    // the warmup that follows the duel brings them back, so this lasts exactly as long as it should.
    // count_bots only - real players are swept to spectator as before.
    private void StandDownTestBots(ulong keep1, ulong keep2)
    {
        if (!Config.CountBots) return;
        foreach (var player in HumanPlayers())
        {
            if (!player.IsBot || Id(player) == keep1 || Id(player) == keep2) continue;
            if (player.PawnIsAlive) player.CommitSuicide(false, true);
        }
    }

    // CSS runs repeat timers by walking its vector backwards by index, and when the timer it is inside
    // asked to die it erases that index. Killing a *different* repeat timer from inside a tick erases it
    // out of the same vector, every later index shifts down one, and the walk then erases the neighbour
    // and deletes the timer it was running - the vector keeps a dangling pointer and the server segfaults
    // on the next frame. [SOURCE: CounterStrikeSharp 1.0.376 src/core/timer_system.cpp
    // TimerSystem::RunFrame and ::KillTimer, read 2026-10-15]
    //
    // So nothing that ends a phase runs inside a tick: ending a phase stops the HUD and the spectate
    // guard, which are repeat timers of their own. Server.NextFrame work is run from the OnTick callback,
    // which OnGameFrame fires once RunFrame has finished walking, so a frame later is outside the walk.
    private static void AfterTick(Action work) => Server.NextFrame(work);

    // A test run's bots do not come back by themselves: a map change drops them, and bot_join_after_player
    // keeps the quota from refilling an empty server. Re-asserting the quota on the new map is what makes
    // the reconnect wait, the draft and the handoff testable without nine people.
    private void RestoreTestBots()
    {
        KeepTestBotsTopped(_expectedBots);
    }

    // Chat reaches players; the log reaches an admin working over rcon, who sees no chat at all.
    private void Announce(string message)
    {
        Server.PrintToChatAll(Prefix() + message);
        Logger.LogInformation("{Message}", message);
    }

    private static string Colorize(string text)
    {
        return text
            .Replace("{default}", ChatColors.Default.ToString())
            .Replace("{white}", ChatColors.White.ToString())
            .Replace("{green}", ChatColors.Green.ToString())
            .Replace("{lime}", ChatColors.Lime.ToString())
            .Replace("{red}", ChatColors.Red.ToString())
            .Replace("{yellow}", ChatColors.Yellow.ToString())
            .Replace("{grey}", ChatColors.Grey.ToString())
            .Replace("{blue}", ChatColors.Blue.ToString())
            .Replace("{gold}", ChatColors.Gold.ToString());
    }

    private static void Reply(CommandInfo command, string message)
    {
        command.ReplyToCommand(message);
    }
}
