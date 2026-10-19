using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;

namespace Gpp;

public partial class Gpp
{
    private void HandOffToMatchZy()
    {
        if (!Config.AutoLoadMatch)
        {
            Announce("auto_load_match is off - teams are set, load a match yourself.");
            return;
        }

        string mapLabel = _votedMap?.Label ?? Server.MapName;

        // Everything that locks a MatchZy match to its roster is gated on isMatchSetup, and
        // matchzy_loadmatch is the only thing that sets it: a loaded match kicks anyone who is not in
        // team1/team2/spectators, forces roster players back onto their drafted side, and moves everyone
        // else to spectator. [SOURCE: MatchZy 0.9.1 EventHandlers.cs EventPlayerConnectFullHandler,
        // Utility.cs UpdatePlayersMap/SwitchPlayerTeam, MatchManagement.cs LoadMatchFromJSON, read
        // 2026-10-19]
        //
        // This server wants the opposite: once a pug is running, anyone may walk in and pick a side. So
        // the normal handoff loads no config at all and starts the match through MatchZy's own ready flow.
        // Only the veto flow still needs a config, because the veto is MatchZy's and it reads its map pool
        // from one.
        bool veto = Config.Flow == "draft_then_veto";
        if (!veto)
        {
            _votedMap = null;
            StartOpenMatch(mapLabel);
            return;
        }

        var maplist = new JsonArray();
        if (veto)
        {
            foreach (var map in Config.MapPool) maplist.Add(map.MatchZyRef);
        }
        else
        {
            // The vote already changed the map, so name the map we are on. MatchZy compares maplist[0] to
            // Server.MapName and reloads the map when they differ - and a workshop id never matches that name,
            // so putting the id here would bounce everyone through a reload right after the draft.
            maplist.Add(Server.MapName);
        }

        if (maplist.Count == 0)
        {
            Announce("No maps to play - fill map_pool in the config.");
            return;
        }

        var config = new JsonObject
        {
            ["matchid"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["num_maps"] = 1,
            // The bigger of the two teams: MatchZy uses this as the per-team limit, and a 3v2 has to fit.
            ["players_per_team"] = Math.Max(1, Math.Max(_team1.Count, _team2.Count)),
            ["maplist"] = maplist,
            // A pool larger than num_maps is vetoed unless we say otherwise, so be explicit either way.
            ["skip_veto"] = !veto,
            ["veto_first"] = "random",
            ["side_type"] = Config.KnifeRound ? "always_knife" : "never_knife",
            ["clinch_series"] = true,
            ["cvars"] = new JsonObject
            {
                // Restored when the series ends, as long as matchzy_reset_cvars_on_series_end is on (it is by
                // default). Both of these are MatchZy kicking people on connect, and this server never
                // kicks anyone - not even mid-match.
                ["matchzy_whitelist_enabled_default"] = "false",
                ["matchzy_kick_when_no_match_loaded"] = "false",
                // Uneven teams are the point: the engine must not even the sides up or refuse a join.
                ["mp_autoteambalance"] = "0",
                ["mp_limitteams"] = "0",
                // Warmup lifts the per-round buy cap (see SyncBuyCap) and live.cfg says nothing about it,
                // so a match has to ask for the engine default itself.
                ["mp_weapons_allow_typecount"] = "5",
            },
            ["team1"] = TeamObject(_captain1, _team1),
            ["team2"] = TeamObject(_captain2, _team2),
        };

        // Written every time rather than left to matchzy_minimum_ready_required, so the number does not
        // depend on a server cvar nobody remembers setting.
        int realPlayers = _team1.Concat(_team2).Count(steamId => !IsBotId(steamId));
        config["min_players_to_ready"] = Config.MinReady > 0
            ? Math.Max(1, Math.Min(Config.MinReady, realPlayers))
            : Math.Max(1, realPlayers);

        string fileName = $"gpp-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json";
        string relativePath = string.IsNullOrWhiteSpace(Config.MatchConfigDir)
            ? fileName
            : $"{Config.MatchConfigDir.Trim('/')}/{fileName}";
        string absoluteDir = Path.Combine(Server.GameDirectory, "csgo", Config.MatchConfigDir.Trim('/'));
        string absolutePath = Path.Combine(absoluteDir, fileName);

        try
        {
            Directory.CreateDirectory(absoluteDir);
            File.WriteAllText(absolutePath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Logger.LogError("Could not write {Path}: {Message}", absolutePath, exception.Message);
            Announce("Could not write the match config - check the server log.");
            return;
        }

        Logger.LogInformation("Wrote match config {Path}", absolutePath);
        _votedMap = null;

        // matchzy_loadmatch is refused outright while a match is still set up, and the refusal goes to the
        // server console where nobody sees it. Anything set up at the end of a draft is stale - a match from
        // an earlier pug, or one loaded before GPP was reloaded and lost track of it - so clear it
        // unconditionally rather than trusting _matchLoaded.
        Server.ExecuteCommand("get5_endmatch");
        _matchLoaded = false;

        // A series end restores the cvars MatchZy changed for it, which can put a whitelist from an older
        // pug back on - right as this match loads, with everyone on the server outside its roster.
        NeverKickAnyone();

        // Last chance before the match: nobody starts a round with a warped camera or a spun view.
        ClearFunEffects();

        // The draft ran with the round held still; hand MatchZy a normal warmup to start its ready phase in.
        ReleaseTheRound();

        string team1Name = (string)config["team1"]!["name"]!;
        string team2Name = (string)config["team2"]!["name"]!;
        AddTimer(0.5f, () => LoadMatch(relativePath, team1Name, team2Name, veto, mapLabel));
    }

    // The open handoff: MatchZy runs the match, the draft just decides who starts where.
    //
    // What a match config would have carried has to be said out loud instead - knife round, the ready
    // threshold, team names - and two things it used to do are gone on purpose: nothing kicks a latecomer,
    // and nothing drags a player back to their drafted side once the match is live.
    private void StartOpenMatch(string mapLabel)
    {
        string team1Name = TeamName(_captain1);
        string team2Name = TeamName(_captain2);

        // Bots play the match but cannot ready up, so they do not count towards the threshold.
        int realPlayers = _team1.Concat(_team2).Count(steamId => !IsBotId(steamId));
        int minReady = Config.MinReady > 0 ? Math.Min(Config.MinReady, realPlayers) : realPlayers;

        // Last chance before the match: nobody starts a round with a warped camera or a spun view.
        ClearFunEffects();

        // The draft ran with the round held still; hand MatchZy a normal warmup to start its ready phase in.
        ReleaseTheRound();

        // Anything still set up at the end of a draft is stale - a match from an earlier pug, or one loaded
        // before GPP was reloaded and lost track of it.
        Server.ExecuteCommand("get5_endmatch");
        _matchLoaded = false;

        // A series end restores the cvars MatchZy changed for it, which can put a whitelist from an older
        // pug back on - right as this match starts, with half the server outside its roster.
        NeverKickAnyone();

        AddTimer(0.5f, () =>
        {
            Server.ExecuteCommand($"matchzy_knife_enabled_default {(Config.KnifeRound ? "true" : "false")};"
                + $"matchzy_minimum_ready_required {Math.Max(1, minReady)};"
                + "mp_autoteambalance 0;mp_limitteams 0");

            // Unquoted on purpose: MatchZy takes the name from the raw argument text, and TeamName() only
            // ever produces letters, digits and an underscore. Set before match mode, because starting it
            // writes mp_teamname_1/2 back out of MatchZy's own team data.
            Server.ExecuteCommand($"css_team1 {team1Name};css_team2 {team2Name}");

            // css_match starts match mode from sleep or practice only, and css_sleep refuses while a match
            // is started - so the order here is end, sleep, match, or the second pug of a session gets no
            // ready phase at all and nobody can see why. [SOURCE: MatchZy 0.9.1 Utility.cs StartMatchMode,
            // SleepMode.cs OnSleepCommand, read 2026-10-19]
            Server.ExecuteCommand("css_sleep;css_match");
            AddTimer(1.5f, () => ConfirmOpenMatch(team1Name, team2Name, mapLabel));
        });
    }

    // MatchZy exposes no way to ask whether match mode took. css_team1/2 write our names into
    // mp_teamname_1/2 and starting match mode writes them again, so read those back.
    private void ConfirmOpenMatch(string team1Name, string team2Name, string mapLabel)
    {
        string name1 = ConVar.Find("mp_teamname_1")?.StringValue ?? "";
        string name2 = ConVar.Find("mp_teamname_2")?.StringValue ?? "";

        if (name1 != team1Name || name2 != team2Name)
        {
            Logger.LogError("Match mode did not take - mp_teamname_1/2 are \"{Name1}\"/\"{Name2}\", expected "
                + "\"{Team1}\"/\"{Team2}\". Check the server console for MatchZy's reason.",
                name1, name2, team1Name, team2Name);
            Announce($"{Colorize("{red}")}MatchZy did not start the match{Colorize("{default}")} - teams are "
                + "set, start it with .match (server console has the reason).");
            return;
        }

        _matchLoaded = true;
        NeverKickAnyone();
        MoveDiscordTeams();
        StartReadyGate();
        Prompt($"Teams set on {mapLabel}.", ".ready", Everybody);
        StartReadyHud(mapLabel);
    }

    private void LoadMatch(string relativePath, string team1Name, string team2Name, bool veto, string mapLabel)
    {
        // Unquoted on purpose. MatchZy takes the filename from CommandInfo.ArgString, which is the raw
        // argument text, so quotes end up inside the path it joins onto csgo/ and every load fails with
        // "Provided file does not exist" - printed to the server console only. [SOURCE: MatchZy 0.9.1
        // MatchManagement.cs LoadMatch]
        Server.ExecuteCommand($"matchzy_loadmatch {relativePath}");
        AddTimer(1.5f, () => ConfirmMatchLoaded(relativePath, team1Name, team2Name, veto, mapLabel));
    }

    // MatchZy exposes no way to ask whether the load took. What a successful load does do is call
    // SetTeamNames(), which writes our team names into mp_teamname_1/2, so read those back.
    private void ConfirmMatchLoaded(string relativePath, string team1Name, string team2Name, bool veto, string mapLabel)
    {
        string name1 = ConVar.Find("mp_teamname_1")?.StringValue ?? "";
        string name2 = ConVar.Find("mp_teamname_2")?.StringValue ?? "";
        bool loaded = name1 == team1Name || name2 == team1Name || name1 == team2Name || name2 == team2Name;

        if (!loaded)
        {
            Logger.LogError("matchzy_loadmatch {Path} did not take - mp_teamname_1/2 are \"{Name1}\"/\"{Name2}\", "
                + "expected one of \"{Team1}\"/\"{Team2}\". Check the server console for MatchZy's reason.",
                relativePath, name1, name2, team1Name, team2Name);
            Announce($"{Colorize("{red}")}MatchZy did not load the match{Colorize("{default}")} - teams are set, "
                + $"load it with matchzy_loadmatch {relativePath} (server console has the reason).");
            return;
        }

        _matchLoaded = true;
        NeverKickAnyone();
        MoveDiscordTeams();
        StartReadyGate();
        Prompt(veto ? "Match loaded - captains veto maps first." : $"Teams set on {mapLabel}.",
            ".ready", Everybody);
        StartReadyHud(veto ? "the vetoed map" : mapLabel);
    }

    private JsonObject TeamObject(ulong captain, List<ulong> roster)
    {
        // A bot has no SteamID to put in a roster: it plays the match, it just is not in the config.
        var players = new JsonObject();
        foreach (var steamId in roster.Where(id => !IsBotId(id))) players[steamId.ToString()] = Name(steamId);

        return new JsonObject
        {
            ["name"] = TeamName(captain),
            ["players"] = players,
        };
    }

    // MatchZy strips special characters from team names itself; keep it short and printable.
    private string TeamName(ulong captain)
    {
        string name = new(Name(captain).Where(char.IsLetterOrDigit).ToArray());
        if (name.Length > 12) name = name[..12];
        return string.IsNullOrEmpty(name) ? "team" : $"team_{name}";
    }
}
