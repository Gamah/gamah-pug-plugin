using System.Globalization;
using System.Net;
using System.Text;

namespace Gpp;

// The whole site's HTML. Server-rendered strings rather than a template engine or a client-side app:
// there are four pages, they read one SQLite file, and anything that needs a build step to show a
// scoreboard will rot faster than the scoreboard changes.
public static class Pages
{
    // Everything that reaches a page goes through this. The one piece of player-controlled text in the
    // database is a name, and a name can be anything a Steam profile can be.
    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    // The plugin writes "s" format UTC. Rendered with the zone named, because the box runs BST and a
    // scoreboard an hour out is the kind of wrong nobody reports.
    public static string Stamp(string? utc)
    {
        if (string.IsNullOrEmpty(utc)) return "";
        return DateTime.TryParse(utc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : utc;
    }

    public static string Ratio(int kills, int deaths) =>
        (deaths == 0 ? kills : kills / (double)deaths).ToString("0.00", CultureInfo.InvariantCulture);

    // A team name is written as "team_Ada"; the prefix is the plugin's, not something to show.
    public static string Team(string name) =>
        name.StartsWith("team_", StringComparison.Ordinal) ? name[5..] : name;

    private const string Style = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin:0; padding:1.5rem 1rem 4rem; background:#14161a; color:#e8e8e8;
               font:16px/1.5 system-ui, sans-serif; }
        main { max-width: 60rem; margin: 0 auto; }
        a { color:#8ed0ff; text-decoration:none; } a:hover { text-decoration:underline; }
        h1 { font-size:1.4rem; margin:0 0 .25rem; } h2 { font-size:1.1rem; margin:2rem 0 .5rem; }
        nav { margin:0 0 1.5rem; color:#9aa0a6; } nav a { margin-right:1rem; }
        table { border-collapse:collapse; width:100%; }
        th, td { text-align:left; padding:.4rem .6rem; border-bottom:1px solid #2a2e35;
                 white-space:nowrap; }
        th { color:#9aa0a6; font-weight:600; font-size:.85rem; }
        td.num, th.num { text-align:right; font-variant-numeric:tabular-nums; }
        .dim { color:#9aa0a6; } .live { color:#ffd479; }
        .score { font-variant-numeric:tabular-nums; font-weight:600; }
        @media (max-width:40rem) { body { padding:1rem .5rem 3rem; } th, td { padding:.35rem .4rem; } }
        """;

    public static string Layout(string title, string body) => $"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{H(title)}</title><style>{Style}</style>
        </head><body><main>
        <nav><a href="/">Matches</a><a href="/players">Players</a><a href="/demos">Demos</a></nav>
        {body}
        </main></body></html>
        """;

    public static string Matches(IReadOnlyList<StatsQueries.MatchRow> matches, int page, bool more)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Match history</h1>");
        if (matches.Count == 0)
        {
            sb.Append("<p class=dim>No matches recorded yet.</p>");
            return Layout("Matches", sb.ToString());
        }

        sb.Append("<table><tr><th>When</th><th>Map</th><th>Match</th><th class=num>Score</th>"
                + "<th class=num>Players</th></tr>");
        foreach (var m in matches)
        {
            string score = m.Finished
                ? $"{m.Score1}&ndash;{m.Score2}"
                : "<span class=live>live or abandoned</span>";
            sb.Append($"<tr><td class=dim>{H(Stamp(m.StartedAt))}</td><td>{H(m.Map)}</td>"
                    + $"<td><a href=\"/match/{m.MatchId}\">{H(Team(m.Team1))} vs {H(Team(m.Team2))}</a></td>"
                    + $"<td class=\"num score\">{score}</td><td class=num>{m.Players}</td></tr>");
        }
        sb.Append("</table><p>");
        if (page > 0) sb.Append($"<a href=\"/?page={page - 1}\">&larr; newer</a> ");
        if (more) sb.Append($"<a href=\"/?page={page + 1}\">older &rarr;</a>");
        sb.Append("</p>");
        return Layout("Matches", sb.ToString());
    }

    public static string Match(StatsQueries.MatchRow match, IReadOnlyList<StatsQueries.ScoreRow> rows,
        IReadOnlyList<string> demos)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>{H(Team(match.Team1))} vs {H(Team(match.Team2))}</h1>");
        sb.Append($"<p class=dim>{H(match.Map)} &middot; {H(Stamp(match.StartedAt))}");
        if (match.Finished)
        {
            sb.Append($" &middot; <span class=score>{match.Score1}&ndash;{match.Score2}</span>");
            if (!string.IsNullOrEmpty(match.Winner))
                sb.Append($" &middot; {H(Team(match.Winner))} won");
        }
        else
        {
            sb.Append(" &middot; <span class=live>no result recorded</span>");
        }
        sb.Append("</p>");

        // Grouped by team so a scoreboard reads like one, with the sides in the order the plugin names
        // them rather than by score.
        foreach (var team in new[] { match.Team1, match.Team2 }
                     .Concat(rows.Select(r => r.Team)).Distinct().Where(
                         t => rows.Any(r => r.Team == t)))
        {
            sb.Append($"<h2>{H(Team(team))}</h2>");
            sb.Append(ScoreTable(rows.Where(r => r.Team == team).ToList()));
        }

        var unassigned = rows.Where(r => string.IsNullOrEmpty(r.Team)).ToList();
        if (unassigned.Count > 0)
        {
            sb.Append("<h2>No team</h2>").Append(ScoreTable(unassigned));
        }

        if (demos.Count > 0)
        {
            sb.Append("<h2>Demos</h2><ul>");
            foreach (var demo in demos)
                sb.Append($"<li><a href=\"/demos/{Uri.EscapeDataString(demo)}\">{H(demo)}</a></li>");
            sb.Append("</ul>");
        }
        return Layout($"Match {match.MatchId}", sb.ToString());
    }

    private static string ScoreTable(IReadOnlyList<StatsQueries.ScoreRow> rows)
    {
        var sb = new StringBuilder("<table><tr><th>Player</th><th class=num>K</th><th class=num>D</th>"
            + "<th class=num>A</th><th class=num>K/D</th><th class=num>Damage</th><th class=num>HS</th>"
            + "<th class=num>MVP</th></tr>");
        foreach (var r in rows)
        {
            sb.Append($"<tr><td><a href=\"/player/{r.SteamId}\">{H(r.Name)}</a></td>"
                    + $"<td class=num>{r.Kills}</td><td class=num>{r.Deaths}</td>"
                    + $"<td class=num>{r.Assists}</td><td class=num>{Ratio(r.Kills, r.Deaths)}</td>"
                    + $"<td class=num>{r.Damage}</td><td class=num>{r.Headshots}</td>"
                    + $"<td class=num>{r.Mvps}</td></tr>");
        }
        return sb.Append("</table>").ToString();
    }

    public static string Players(IReadOnlyList<StatsQueries.PlayerTotals> players)
    {
        var sb = new StringBuilder("<h1>Players</h1>");
        if (players.Count == 0) return Layout("Players", sb.Append("<p class=dim>Nobody yet.</p>").ToString());

        sb.Append("<table><tr><th>Player</th><th class=num>Matches</th><th class=num>Won</th>"
                + "<th class=num>K</th><th class=num>D</th><th class=num>A</th><th class=num>K/D</th>"
                + "<th class=num>MVP</th></tr>");
        foreach (var p in players)
        {
            sb.Append($"<tr><td><a href=\"/player/{p.SteamId}\">{H(p.Name)}</a></td>"
                    + $"<td class=num>{p.Matches}</td><td class=num>{p.Wins}</td>"
                    + $"<td class=num>{p.Kills}</td><td class=num>{p.Deaths}</td>"
                    + $"<td class=num>{p.Assists}</td><td class=num>{Ratio(p.Kills, p.Deaths)}</td>"
                    + $"<td class=num>{p.Mvps}</td></tr>");
        }
        return Layout("Players", sb.Append("</table>").ToString());
    }

    public static string Player(StatsQueries.PlayerTotals totals,
        IReadOnlyList<(StatsQueries.MatchRow Match, StatsQueries.ScoreRow Score)> matches)
    {
        var sb = new StringBuilder($"<h1>{H(totals.Name)}</h1>");
        string played = totals.Matches == 1 ? "1 match" : $"{totals.Matches} matches";
        sb.Append($"<p class=dim>{played}, {totals.Wins} won &middot; "
                + $"<a href=\"https://steamcommunity.com/profiles/{totals.SteamId}\">{totals.SteamId}</a></p>");
        sb.Append("<table><tr><th class=num>K</th><th class=num>D</th><th class=num>A</th>"
                + "<th class=num>K/D</th><th class=num>Damage</th><th class=num>HS</th>"
                + "<th class=num>MVP</th></tr>");
        sb.Append($"<tr><td class=num>{totals.Kills}</td><td class=num>{totals.Deaths}</td>"
                + $"<td class=num>{totals.Assists}</td>"
                + $"<td class=num>{Ratio(totals.Kills, totals.Deaths)}</td>"
                + $"<td class=num>{totals.Damage}</td><td class=num>{totals.Headshots}</td>"
                + $"<td class=num>{totals.Mvps}</td></tr></table>");

        sb.Append("<h2>Matches</h2><table><tr><th>When</th><th>Map</th><th>Match</th>"
                + "<th class=num>Score</th><th class=num>K</th><th class=num>D</th>"
                + "<th class=num>A</th></tr>");
        foreach (var (m, s) in matches)
        {
            string score = m.Finished ? $"{m.Score1}&ndash;{m.Score2}" : "<span class=live>&ndash;</span>";
            sb.Append($"<tr><td class=dim>{H(Stamp(m.StartedAt))}</td><td>{H(m.Map)}</td>"
                    + $"<td><a href=\"/match/{m.MatchId}\">{H(Team(m.Team1))} vs {H(Team(m.Team2))}</a></td>"
                    + $"<td class=\"num score\">{score}</td><td class=num>{s.Kills}</td>"
                    + $"<td class=num>{s.Deaths}</td><td class=num>{s.Assists}</td></tr>");
        }
        return Layout(totals.Name, sb.Append("</table>").ToString());
    }

    public static string Demos(IReadOnlyList<(string Name, long Bytes, DateTime Modified)> demos,
        bool configured)
    {
        var sb = new StringBuilder("<h1>Demos</h1>");
        if (!configured)
            return Layout("Demos", sb.Append("<p class=dim>No demo directory is configured.</p>").ToString());
        if (demos.Count == 0)
            return Layout("Demos", sb.Append("<p class=dim>No demos on disk.</p>").ToString());

        sb.Append("<table><tr><th>File</th><th class=num>Size</th><th>Recorded</th></tr>");
        foreach (var (name, bytes, modified) in demos)
        {
            sb.Append($"<tr><td><a href=\"/demos/{Uri.EscapeDataString(name)}\">{H(name)}</a></td>"
                    + $"<td class=num>{Size(bytes)}</td>"
                    + $"<td class=dim>{modified.ToUniversalTime():yyyy-MM-dd HH:mm} UTC</td></tr>");
        }
        return Layout("Demos", sb.Append("</table>").ToString());
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string NotFound(string what) =>
        Layout("Not found", $"<h1>Not found</h1><p class=dim>{H(what)}</p>");
}
