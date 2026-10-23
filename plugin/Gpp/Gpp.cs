using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
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
    public override string ModuleDescription => "Map vote, random captains, a captain draft and the match that follows.";

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
            Logger.LogWarning("map_pool is empty: !pug vote has nothing to offer.");
        }
    }

    public override void Load(bool hotReload)
    {
        QuietenMatchZy();

        HookMatch();
        HookGuardedCommands();
        HookJoins();
        HookCaptainKnife();
        HookFunCommands();
        StartWarmupWatch();
        StartRepaintLoop();
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        // The world panel. CheckTransmit is what keeps each panel to the one person it is addressed to -
        // without it a ten-man pug is ten boards hanging in the same room - and OnTick only does any work
        // on the fallback tier that has to move the panel itself. Both are attached by the panels
        // themselves, for as long as there are boards up: see SyncPanelListeners.
        ProbeFollow();

        // Who holds the warmup open on a server that has only just started: nothing else does now. The
        // OnMapStart listener above is no use for it - a plugin loads after the map it loads into - and
        // the gamerules entity does not exist for the first moment or two, so the check waits.
        //
        // A live match is left alone either way. Reloading the dll mid-match loses the plugin's own match
        // state, which is bad enough without also resetting the engine's.
        AddTimer(2.0f, () =>
        {
            if (!MatchRunning && InWarmupPeriod()) ExecWarmupCfg();
        });
    }

    // A hot reload is how this plugin is deployed - writing the dll *is* the reload - so unload has to
    // take the world panels with it. CSS does not remove a plugin's entities, and a point_worldtext left
    // behind hangs in front of somebody's face until the map changes, with no plugin left to clear it.
    public override void Unload(bool hotReload)
    {
        // A listener registered by this instance can outlive it. CounterStrikeSharp does not reliably
        // take command listeners with the plugin, so after six hot reloads in a session a dot command
        // was answered six times - once per instance still listening. The dedupe in Fun.cs cannot see
        // that, because each instance has its own copy of it and each one thinks the message is new.
        //
        // Unload is called on the old instance before the new one loads, so this is the one moment an
        // outgoing instance can be told to stop answering. Everything it still listens to checks it.
        _unloaded = true;
        ClearWorldPanels();
    }

    // Set by Unload: this instance has been replaced and must not act on anything it still hears.
    private bool _unloaded;

    internal bool Retired => _unloaded;

    // MatchZy is still installed next to GPP until a pug has been run without it. Asleep it does nothing,
    // but two of its cvars outlive the match that set them and kick people on connect - the whitelist for
    // anyone outside a loaded roster, the other for everyone at all when no match is loaded - which is how
    // a finished pug silently kicked the next person to connect hours later. Nobody is ever kicked off
    // this server, so both are turned off here, on every map change, and at every match start.
    //
    // Sleep mode matters for a different reason: left on autostart, MatchZy arms a repeating "N players
    // are not ready" chat timer on the first connect and turns its own .ready on, over the top of GPP's.
    // [SOURCE: MatchZy 0.9.1 Utility.cs AutoStart/SendUnreadyPlayersMessage, SleepMode.cs, read 2026-10-19]
    private void QuietenMatchZy()
    {
        if (!Config.Match.MatchZyInstalled) return;
        Server.ExecuteCommand("matchzy_whitelist_enabled_default false;matchzy_kick_when_no_match_loaded false;"
            + "matchzy_autostart_mode 0");
    }

    // How many bots a test run wants on the server, and the timer that keeps putting them back.
    private int _botTarget;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _botTopUpTimer;

    // Topping up once does not hold. The quota is cleared again under a test run by anything that execs a
    // phase cfg or restarts the warmup - MatchZy's warmup.cfg opened with `bot_quota 0`, which is why
    // GPP's copy deliberately does not, and bot_join_after_player still empties an unattended server. The
    // first version of this stopped as soon as it saw the bots and the draft still got an empty server, so
    // the top-up keeps running for as long as a test wants bots and only !pug bots 0 turns it off.
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
        // an earlier test, and what repairs a quota something else has reset. bot_add is only for the shortfall,
        // and the quota caps whatever it does.
        Server.ExecuteCommand($"bot_join_after_player 0;bot_quota_mode normal;bot_quota {_botTarget}");

        int here = HumanPlayers().Count(player => player.IsBot);
        for (int i = here; i < _botTarget; i++) Server.ExecuteCommand("bot_add");
    }

    private void OnMapStart(string mapName)
    {
        QuietenMatchZy();
        // Entities do not survive a map change, but the dictionary pointing at them does, and a stale
        // wrapper is how a panel ends up being updated into a dead handle. Sweeping the world as well
        // catches anything a crash or a hot reload left behind.
        SweepStrayPanels();
        ClearFunEffects();
        CloseIdlePrompt();
        // The new map is as fresh as it gets, so the clock starts again from here.
        _emptySince = HumanPlayers().Any(player => !player.IsBot) ? null : DateTime.UtcNow;

        // A map change ends any match that was running - including the restart at the end of one - and the
        // new map starts in whatever state its own cfgs leave it. The held warmup is this plugin's job now
        // that MatchZy's sleep mode is not doing it.
        _matchPhase = MatchPhase.None;
        ResetPauses();
        AddTimer(1.0f, ExecWarmupCfg);

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
        // Their panel goes with them: a point_worldtext left parented to a pawn that no longer exists
        // stays in the world until the map does.
        HideWorldPanel(slot);

        // Next frame: this runs before the player is actually gone, so liveness checks still see them.
        Server.NextFrame(() =>
        {
            if (_phase == PugPhase.Draft) HandleDraftDisconnect(steamId);
            else if (_phase == PugPhase.MapVote) _votes.Remove(steamId);
        });
    }

    [ConsoleCommand("css_pug", "GPP admin command: start | vote | draft | captains | match | endmatch | stop | cancel | status | bots <n> | hudsize | hudboth | hudlist [abcd]")]
    [CommandHelper(minArgs: 0, usage: "<start|vote|draft|captains|match|endmatch|stop|cancel|status|hudsize|hudlist>")]
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
            case "vote":
                _teamSizeOverride = size;
                // The captain grace period goes in front of the vote, not the draft: by the time the map
                // has changed and everyone has reconnected, nobody is reading chat for a prompt.
                AfterTick(() =>
                {
                    if (!OpenCaptainOptIn(() => StartMapVote(force), force)) StartMapVote(force);
                });
                break;
            case "draft":
                _teamSizeOverride = size;
                AfterTick(() =>
                {
                    if (!OpenCaptainOptIn(() => StartDraft(force), force)) StartDraft(force);
                });
                break;
            case "captains":
                // !pug captains                      reroll at random
                // !pug captains humans               the two people on the server, bots left out
                // !pug captains <name> <name>        exactly these two
                if (command.ArgCount > 3)
                {
                    string one = command.ArgByIndex(2);
                    string two = command.ArgByIndex(3);
                    AfterTick(() => SetCaptains(command, one.ToLowerInvariant() == "humans" ? "humans" : one, two));
                }
                else if (arg == "humans")
                {
                    AfterTick(() => SetCaptains(command, "humans", ""));
                }
                else
                {
                    RerollCaptains(command);
                }
                break;
            case "match":
                AfterTick(() =>
                {
                    if (MatchRunning) Announce($"Already running: {MatchStatus()}");
                    else if (_team1.Count == 0 && _team2.Count == 0) Announce("No teams drafted yet.");
                    else StartMatch(Server.MapName);
                });
                break;
            case "endmatch":
                AfterTick(() => AbandonMatch("An admin ended it."));
                break;
            case "go":
                AfterTick(() => EndOpeningPause("An admin started it."));
                break;
            case "idle":
                // Put the idle panel up on demand. Working on it otherwise means emptying the server and
                // waiting out the threshold for every change to a line of text.
                AfterTick(() => OpenIdlePrompt(TimeSpan.FromHours(10)));
                break;
            case "keepmap":
                AfterTick(() => CancelRestart("An admin kept the server up."));
                break;
            case "restart":
                AfterTick(() => AskForRestart($"an admin asked for it"));
                break;
            case "cancel":
                AfterTick(() => Cancel("An admin cancelled it."));
                break;
            case "stop":
            case "reset":
                AfterTick(() => StopEverything("An admin stopped it."));
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
            case "sides":
                // Where the two team boards sit. Derived from how wide the boards are; this is for
                // moving them by eye on a client the numbers were not measured on.
                Reply(command, SetSides(arg,
                    command.ArgCount > 3 ? command.ArgByIndex(3) : "",
                    command.ArgCount > 4 ? command.ArgByIndex(4) : ""));
                break;
            case "discord":
                Reply(command, SetOpeningBoards(arg));
                break;
            case "scroll":
                ScrollProbe();
                break;
            case "wrap":
                WrapProbe();
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
        if (CaptainOptInOpen)
        {
            return $"Picking captains - {_optInSecondsLeft}s left, {VolunteerCount()} volunteer(s).";
        }
        if (RestartPending)
        {
            return $"Server restarting in {_restartSecondsLeft}s - {_restartWhy}.";
        }
        if (IdlePromptUp)
        {
            return $"Server was idle for {Describe(_idleWas)} - waiting for !reload, !restart or !skip.";
        }

        return _phase switch
        {
            PugPhase.MapVote => $"Map vote running, {_votes.Count} vote(s) in, {_voteSecondsLeft}s left.",
            PugPhase.CaptainKnife => $"Captains knifing for first pick, {_knifeSecondsLeft}s left.",
            PugPhase.Draft => DraftStatus(),
            _ => MatchRunning
                ? MatchStatus()
                : $"Idle. {HumanPlayers().Count} player(s) connected.",
        };
    }

    private void Cancel(string why)
    {
        if (CaptainOptInOpen)
        {
            CloseCaptainOptIn(draw: false);
            Announce($"Captain sign-up cancelled. {why}");
            return;
        }
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

    // The flag check goes through the controller's own steam id, not AuthorizedSteamID. CS2 1.41.9.2
    // left CounterStrikeSharp 1.0.376 reading 730 instead of a CSteamID pointer, so the overload that
    // takes a player dereferences that and segfaults the server - every admin command took the box
    // down with it (CounterStrikeSharp#1466). m_steamID is a schema field and still correct; a player
    // whose id has not arrived yet reads 0, which matches nobody's admin entry.
    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return true; // server console / rcon
        if (player.IsBot || player.IsHLTV) return false;
        var steamId = player.SteamID;
        if (steamId == 0) return false;
        return AdminManager.PlayerHasPermissions(new SteamID(steamId), Config.AdminFlag);
    }

    // Connected players, excluding GOTV. count_bots pulls bots in for a solo test run.
    private List<CCSPlayerController> HumanPlayers()
    {
        return Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsHLTV && (!p.IsBot || Config.CountBots))
            .ToList();
    }

    // Only the people who can actually answer a prompt. count_bots pulls bots into HumanPlayers so one
    // person can walk a whole pug, which also means a vote or a countdown waiting on "everybody" would
    // wait on nine bots that cannot type: anything that closes early counts these instead.
    private List<CCSPlayerController> AnsweringPlayers()
    {
        return Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsHLTV && !p.IsBot)
            .ToList();
    }

    // Everything here keys players by SteamID, and a bot's is 0 - so every bot would be the same player.
    // Bots get an id from their slot instead, which is stable for as long as they are connected and is all
    // a test run needs. Nothing outside this file needs to know which kind it is holding.
    //
    // The two ranges must not overlap, and the first attempt at this put the bot range *above* the SteamID
    // range: every real player then read as a bot, which emptied the drafted rosters and handed humans the
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
