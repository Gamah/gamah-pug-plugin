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

        bool veto = Config.Flow == "draft_then_veto";
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
        string mapLabel = _votedMap?.Label ?? Server.MapName;
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
