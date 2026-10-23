using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace Gpp;

public class MapEntry
{
    // Map name as the server knows it (de_mirage), used for changelevel when there is no workshop id.
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // Workshop file id. Set for workshop maps: host_workshop_map addresses those by id, not by name.
    [JsonPropertyName("workshop_id")] public string WorkshopId { get; set; } = "";

    // What players see on the ballot. Defaults to Name.
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    // The ballot has to fit in three columns of a fixed-width HUD, and "de_" on the front of two thirds
    // of the pool is eight wasted characters a line that nobody reads anyway. The prefix only comes off
    // the label; Name is untouched, so changelevel and !vote <name> still work on the real map name.
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Display) ? StripPrefix(Name) : Display;

    private static string StripPrefix(string name)
    {
        foreach (var prefix in new[] { "de_", "cs_", "ar_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
        }
        return name;
    }

    [JsonIgnore] public bool IsWorkshop => !string.IsNullOrWhiteSpace(WorkshopId);
}

public class DiscordConfig
{
    // Where the bot's HTTP API lives, e.g. "http://127.0.0.1:8787". Empty disables every Discord feature.
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = "";

    // Shared secret, sent as the X-GPP-Token header. The bot rejects anything without it.
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    // Move the two rosters into their voice channels when the match starts.
    [JsonPropertyName("move_on_handoff")] public bool MoveOnHandoff { get; set; } = true;

    // Send everyone back to the warmup channel when the match ends.
    [JsonPropertyName("return_on_match_end")] public bool ReturnOnMatchEnd { get; set; } = true;

    // Give up on a request after this long. The game thread never waits on one, but a bot that has gone
    // away should not leave requests piling up either.
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 5;

    [JsonIgnore] public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

// The match itself: which cfgs hold the phases, and the three things MatchZy used to do that were worth
// keeping. Round backups are not among them - a match that breaks mid-way cannot be restored, which was
// decided when MatchZy was dropped rather than overlooked.
public class MatchSettings
{
    // Phase configs, relative to csgo/cfg. The plugin execs them; examples/cfg/gpp holds the ones this
    // server uses, copied from MatchZy's. warmup.cfg is the one that holds the warmup open, so an idle
    // server stays idle - without it the plugin falls back to a handful of built-in cvars.
    [JsonPropertyName("warmup_cfg")] public string WarmupCfg { get; set; } = "gpp/warmup.cfg";

    [JsonPropertyName("knife_cfg")] public string KnifeCfg { get; set; } = "gpp/knife.cfg";

    [JsonPropertyName("live_cfg")] public string LiveCfg { get; set; } = "gpp/live.cfg";

    // Who answers the knife round with .stay or .switch: the winning side's captain, or anyone on the
    // winning side. The captain by default - it is their call, and five people typing over each other
    // is how a side gets picked by whoever has the fastest keyboard.
    [JsonPropertyName("side_choice_captain_only")] public bool SideChoiceCaptainOnly { get; set; } = true;

    // A second ready check between the side choice and going live. Off by default: the ready phase before
    // the knife round is the one people expect, and a second one is another thing to wait for.
    [JsonPropertyName("ready_after_sides")] public bool ReadyAfterSides { get; set; } = false;

    // Seconds of held freeze time at the start of round one, while the side that lost the knife moves
    // into their Discord channel. 0 turns it off. The Discord bot moving people does not replace it: it
    // cannot move somebody who has not joined voice yet.
    [JsonPropertyName("opening_pause_seconds")] public int OpeningPauseSeconds { get; set; } = 30;

    // Seconds the engine's end-of-match screen is left up before the server is handed back to a held
    // warmup - mp_match_restart_delay, which is the engine's own clock for it. Nothing may be sent while
    // that screen is up: a warmup, a restart or a phase write inside it all leave the room stuck on it.
    [JsonPropertyName("post_match_seconds")] public int PostMatchSeconds { get; set; } = 15;

    [JsonPropertyName("pauses_enabled")] public bool PausesEnabled { get; set; } = true;

    // Technical pauses a side may call per match. 0 is unlimited.
    [JsonPropertyName("max_tech_pauses")] public int MaxTechPauses { get; set; } = 0;

    // Seconds a technical pause runs before either side alone may .unpause it. 0 means both sides always
    // have to agree.
    [JsonPropertyName("tech_pause_seconds")] public int TechPauseSeconds { get; set; } = 0;

    [JsonPropertyName("demos_enabled")] public bool DemosEnabled { get; set; } = true;

    // Where demos go, relative to csgo/. Needs tv_enable 1 on the server or tv_record does nothing.
    [JsonPropertyName("demo_path")] public string DemoPath { get; set; } = "gpp-demos";

    // {TIME} {MATCHID} {MAP} {TEAM1} {TEAM2}. Anything a filename cannot carry is stripped.
    [JsonPropertyName("demo_name_format")]
    public string DemoNameFormat { get; set; } = "{TIME}_{MAP}_{TEAM1}_vs_{TEAM2}";

    [JsonPropertyName("stats_enabled")] public bool StatsEnabled { get; set; } = true;

    // SQLite file for match and player rows. A bare name sits next to the plugin dll.
    [JsonPropertyName("stats_database")] public string StatsDatabase { get; set; } = "gpp.db";

    // MatchZy is still installed alongside GPP. While this is true the plugin keeps MatchZy asleep and
    // keeps its two kick cvars off, so a leftover whitelist cannot kick anyone mid-pug. Set it false once
    // the MatchZy plugin folder is gone, or the server console fills with unknown-command lines.
    [JsonPropertyName("matchzy_installed")] public bool MatchZyInstalled { get; set; } = true;
}

public class GppConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 1;

    // There is deliberately no "flow" setting any more. It chose between a map vote and MatchZy's own
    // map veto, and the veto went with MatchZy: !pug start votes for a map and then drafts, !pug draft
    // drafts on the map the server is already on.

    // 0 = size the teams from whoever is present when the draft starts, and nobody is left out: an odd
    // headcount drafts uneven teams (5 players -> 3v2). Any other value pins the team size and anyone
    // past it stays in spectator. !pug draft <n> overrides it for one draft.
    [JsonPropertyName("players_per_team")] public int PlayersPerTeam { get; set; } = 0;

    // Players needed before !pug start will run. !pug start force ignores it.
    // Two, because two is a pug: two captains, nothing to draft, and the match runs. A higher floor only
    // ever meant typing "!pug start force" to get past it, which is the same start with the captain
    // sign-up skipped - so the floor was buying nothing and costing the opt-in.
    [JsonPropertyName("min_players")] public int MinPlayers { get; set; } = 2;

    [JsonPropertyName("vote_seconds")] public int VoteSeconds { get; set; } = 30;

    // How many maps, drawn at random from the pool, go on the ballot. 0 puts the whole pool on it.
    [JsonPropertyName("ballot_size")] public int BallotSize { get; set; } = 5;

    // After a map change, how long to wait for the players who were here to come back before drafting.
    [JsonPropertyName("reconnect_wait_seconds")] public int ReconnectWaitSeconds { get; set; } = 90;

    // Seconds a captain has before the pick is made for them at random. 0 disables the timeout.
    [JsonPropertyName("pick_seconds")] public int PickSeconds { get; set; } = 30;

    // Show each captain the pick list on a redrawing centre-screen panel instead of in chat.
    [JsonPropertyName("hud")] public bool Hud { get; set; } = true;

    // Which overlay draws the panels: "center" is PrintToCenter, four lines of eighty characters with a
    // measured glyph table behind it; "world" is a point_worldtext in front of each player, twelve lines
    // and no width budget. The centre panel is kept so a pug night can be rescued by flipping this back
    // rather than rolling the plugin back.
    [JsonPropertyName("hud_style")] public string HudStyle { get; set; } = "world";

    // Where the two team boards sit, in world units either side of the middle one. Left empty the
    // plugin works it out from how wide the boards are, which is what it should be doing - this is here
    // for the night somebody wants them further apart without a redeploy.
    [JsonPropertyName("hud_side_gap")] public float HudSideGap { get; set; } = 0f;

    // "alternate" = 1,2,1,2 ... ; "snake" = 1,2,2,1,1,2,2,1 ...
    [JsonPropertyName("pick_order")] public string PickOrder { get; set; } = "alternate";

    // How long the server has to have been empty before the first person to connect is offered the fix
    // for it: !reload, !restart or !skip, on a panel that stays up until one of them is picked. A server
    // idle for ten hours plays a strange first round - the warmup has been held open that whole time.
    // 0 turns it off.
    [JsonPropertyName("idle_reload_minutes")] public int IdleReloadMinutes { get; set; } = 30;

    // Seconds of warning before a restart (!pug restart, or idle_restart_hours).
    [JsonPropertyName("restart_warning_seconds")] public int RestartWarningSeconds { get; set; } = 30;

    // Seconds after !pug start that anyone can !captain to volunteer, before the captains are drawn
    // from whoever put their hand up. 0 turns the grace period off and captains are drawn at random, as
    // they always were. !pug start force skips it either way.
    [JsonPropertyName("captain_opt_in_seconds")] public int CaptainOptInSeconds { get; set; } = 30;

    // Captains knife 1v1 for first pick before the draft. Off: first pick goes to a coin flip.
    [JsonPropertyName("captain_knife")] public bool CaptainKnife { get; set; } = true;

    // Seconds before the duel is decided on health (and then a coin flip) instead of a kill.
    [JsonPropertyName("captain_knife_seconds")] public int CaptainKnifeSeconds { get; set; } = 60;

    // Move everyone to spectator when the draft starts, so only drafted players are on a team.
    [JsonPropertyName("force_spectate")] public bool ForceSpectate { get; set; } = true;

    // Start the match - ready phase, knife, live - as soon as the draft ends. Off leaves the teams set
    // and waits for !pug match.
    [JsonPropertyName("auto_start_match")] public bool AutoStartMatch { get; set; } = true;

    // Knife round for sides once everybody has readied. Off goes straight live with the drafted sides.
    [JsonPropertyName("knife_round")] public bool KnifeRound { get; set; } = true;

    // How many .ready are needed to start the match. 0 means every drafted player.
    [JsonPropertyName("min_ready")] public int MinReady { get; set; } = 2;

    [JsonPropertyName("match")] public MatchSettings Match { get; set; } = new();

    // There is deliberately no whitelist option, and nothing in this plugin kicks anyone. A whitelist is
    // enforced by kicking everyone who is not on the roster - which is how MatchZy, hours after a pug,
    // kicked whoever connected next, because the cvar outlived the match that set it.

    // Count bots as players, so one person plus `bot_add` can walk the whole flow on an empty server.
    // Bots have no SteamID, so they are tracked by slot and left out of the stats.
    [JsonPropertyName("count_bots")] public bool CountBots { get; set; } = false;

    // The Discord bot is optional and off unless base_url is set. With it empty, !link-discord says so
    // and nothing is ever sent anywhere - the plugin runs exactly as it did before the bot existed.
    [JsonPropertyName("discord")] public DiscordConfig Discord { get; set; } = new();

    // Money every player is topped back up to, once a second, for as long as warmup is running.
    // mp_maxmoney and mp_startmoney only set what you are given, not what you keep. 0 turns it off.
    [JsonPropertyName("warmup_money")] public int WarmupMoney { get; set; } = 16000;

    // Usable width of one centre-panel line, in the glyph units HudMetrics measures in. Depends on the
    // viewer's resolution and HUD scale, not on the font, so it is a setting rather than a constant.
    // 340 is below the widest line measured to fit. Raise it if your players all run one resolution and
    // the panel looks empty; lower it if lines still wrap. !pug ruler re-measures.
    [JsonPropertyName("hud_float_left")] public bool HudFloatLeft { get; set; } = true;

    [JsonPropertyName("hud_float_gap")] public float HudFloatGap { get; set; }

    [JsonPropertyName("hud_line_budget")] public int HudLineBudget { get; set; } = 340;

    [JsonPropertyName("admin_flag")] public string AdminFlag { get; set; } = "@css/config";

    [JsonPropertyName("chat_prefix")] public string ChatPrefix { get; set; } = " [{green}GPP{default}]";

    [JsonPropertyName("map_pool")]
    public List<MapEntry> MapPool { get; set; } =
    [
        new() { Name = "de_mirage" },
        new() { Name = "de_inferno" },
        new() { Name = "de_nuke" },
        new() { Name = "de_ancient" },
        new() { Name = "de_anubis" },
        new() { Name = "de_dust2" },
        new() { Name = "de_train" },
    ];
}
