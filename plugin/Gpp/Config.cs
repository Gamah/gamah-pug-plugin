using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace Gpp;

public class MapEntry
{
    // Map name as the server knows it (de_mirage), used for changelevel and as the MatchZy maplist entry
    // when there is no workshop id.
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // Workshop file id. Set for workshop maps: MatchZy and host_workshop_map address those by id, not by name.
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

    // What goes in a MatchZy maplist / what we hand to the map change command.
    [JsonIgnore] public string MatchZyRef => string.IsNullOrWhiteSpace(WorkshopId) ? Name : WorkshopId;

    [JsonIgnore] public bool IsWorkshop => !string.IsNullOrWhiteSpace(WorkshopId);
}

public class DiscordConfig
{
    // Where the bot's HTTP API lives, e.g. "http://127.0.0.1:8787". Empty disables every Discord feature.
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = "";

    // Shared secret, sent as the X-GPP-Token header. The bot rejects anything without it.
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    // Move the two rosters into their voice channels when the draft is handed to MatchZy.
    [JsonPropertyName("move_on_handoff")] public bool MoveOnHandoff { get; set; } = true;

    // Send everyone back to the warmup channel when the match ends.
    [JsonPropertyName("return_on_match_end")] public bool ReturnOnMatchEnd { get; set; } = true;

    // Give up on a request after this long. The game thread never waits on one, but a bot that has gone
    // away should not leave requests piling up either.
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 5;

    [JsonIgnore] public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

public class GppConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 1;

    // "vote_then_draft": whole server votes a map, server changes to it, then captains draft.
    // "draft_then_veto": captains draft first, then MatchZy's own .ban/.pick veto picks the map.
    [JsonPropertyName("flow")] public string Flow { get; set; } = "vote_then_draft";

    // 0 = size the teams from whoever is present when the draft starts, and nobody is left out: an odd
    // headcount drafts uneven teams (5 players -> 3v2). Any other value pins the team size and anyone
    // past it stays in spectator. !pug draft <n> overrides it for one draft.
    [JsonPropertyName("players_per_team")] public int PlayersPerTeam { get; set; } = 0;

    // Players needed before !pug start will run. !pug start force ignores it.
    [JsonPropertyName("min_players")] public int MinPlayers { get; set; } = 4;

    [JsonPropertyName("vote_seconds")] public int VoteSeconds { get; set; } = 30;

    // How many maps, drawn at random from the pool, go on the ballot. 0 puts the whole pool on it.
    [JsonPropertyName("ballot_size")] public int BallotSize { get; set; } = 5;

    // After a map change, how long to wait for the players who were here to come back before drafting.
    [JsonPropertyName("reconnect_wait_seconds")] public int ReconnectWaitSeconds { get; set; } = 90;

    // Seconds a captain has before the pick is made for them at random. 0 disables the timeout.
    [JsonPropertyName("pick_seconds")] public int PickSeconds { get; set; } = 30;

    // Show each captain the pick list on a redrawing centre-screen panel instead of in chat.
    [JsonPropertyName("hud")] public bool Hud { get; set; } = true;

    // "alternate" = 1,2,1,2 ... ; "snake" = 1,2,2,1,1,2,2,1 ...
    [JsonPropertyName("pick_order")] public string PickOrder { get; set; } = "alternate";

    // Captains knife 1v1 for first pick before the draft. Off: first pick goes to a coin flip.
    [JsonPropertyName("captain_knife")] public bool CaptainKnife { get; set; } = true;

    // Seconds before the duel is decided on health (and then a coin flip) instead of a kill.
    [JsonPropertyName("captain_knife_seconds")] public int CaptainKnifeSeconds { get; set; } = 60;

    // Move everyone to spectator when the draft starts, so only drafted players are on a team.
    [JsonPropertyName("force_spectate")] public bool ForceSpectate { get; set; } = true;

    // Write the MatchZy config and run matchzy_loadmatch when the draft ends.
    [JsonPropertyName("auto_load_match")] public bool AutoLoadMatch { get; set; } = true;

    // Directory for generated match configs, relative to csgo/.
    [JsonPropertyName("match_config_dir")] public string MatchConfigDir { get; set; } = "MatchZy";

    // Knife round for the drafted map. In draft_then_veto the captains pick sides in MatchZy's veto instead.
    [JsonPropertyName("knife_round")] public bool KnifeRound { get; set; } = true;

    // How many .ready votes MatchZy needs to start the drafted match. 0 means every drafted player.
    [JsonPropertyName("min_ready")] public int MinReady { get; set; } = 2;

    // There is deliberately no whitelist option. MatchZy enforces a whitelist by kicking everyone who is
    // not on a loaded match's roster - including, hours later, whoever connects next, because the cvar
    // outlives the match. This server never kicks anyone, so the pug turns it off and keeps it off.

    // Count bots as players, so one person plus `bot_add` can walk the whole flow on an empty server.
    // Bots have no SteamID, so they are tracked by slot and left out of the MatchZy roster.
    [JsonPropertyName("count_bots")] public bool CountBots { get; set; } = false;

    // The Discord bot is optional and off unless base_url is set. With it empty, !link-discord says so
    // and nothing is ever sent anywhere - the plugin runs exactly as it did before the bot existed.
    [JsonPropertyName("discord")] public DiscordConfig Discord { get; set; } = new();

    // Money every player is topped back up to, once a second, for as long as warmup is running.
    // mp_maxmoney and mp_startmoney only set what you are given, not what you keep. 0 turns it off.
    [JsonPropertyName("warmup_money")] public int WarmupMoney { get; set; } = 16000;

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
