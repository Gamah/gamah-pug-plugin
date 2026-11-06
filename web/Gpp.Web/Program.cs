using Gpp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

// The stats site: match history, per-player totals, and the demo files if any exist. It opens the
// plugin's own gpp.db read-only and writes nothing, anywhere - a reader of someone else's database has
// no business holding a lock, creating a table or serving a form.
var settings = Settings.FromEnvironment();
var app = WebApplication.CreateSlimBuilder(args).Build();

const int PageSize = 25;

app.MapGet("/", (int page = 0) =>
{
    using var db = StatsQueries.OpenReadOnly(settings.DatabasePath);
    // One row more than the page, so "older" appears only when there is an older page.
    var rows = StatsQueries.RecentMatches(db, PageSize + 1, Math.Max(page, 0) * PageSize);
    bool more = rows.Count > PageSize;
    return Html(Pages.Matches(rows.Take(PageSize).ToList(), Math.Max(page, 0), more));
});

app.MapGet("/match/{id:long}", (long id) =>
{
    using var db = StatsQueries.OpenReadOnly(settings.DatabasePath);
    var match = StatsQueries.Match(db, id);
    if (match is null) return Html(Pages.NotFound($"No match {id}."), StatusCodes.Status404NotFound);

    var rows = StatsQueries.Scoreboard(db, id);
    return Html(Pages.Match(match, rows, settings.Demos.ForMatch(match)));
});

app.MapGet("/players", () =>
{
    using var db = StatsQueries.OpenReadOnly(settings.DatabasePath);
    return Html(Pages.Players(StatsQueries.Leaderboard(db, 200)));
});

app.MapGet("/player/{steamid}", (string steamid) =>
{
    if (!ulong.TryParse(steamid, out ulong id))
        return Html(Pages.NotFound("That is not a SteamID64."), StatusCodes.Status404NotFound);

    using var db = StatsQueries.OpenReadOnly(settings.DatabasePath);
    var totals = StatsQueries.Totals(db, id);
    if (totals is null)
        return Html(Pages.NotFound($"Nobody with SteamID {id} has played a match here."),
            StatusCodes.Status404NotFound);

    return Html(Pages.Player(totals, StatsQueries.PlayerMatches(db, id, 100)));
});

app.MapGet("/demos", () =>
    Html(Pages.Demos(settings.Demos.List(), settings.Demos.Configured)));

app.MapGet("/demos/{name}", (string name) =>
{
    string? path = settings.Demos.Resolve(name);
    return path is null
        ? Html(Pages.NotFound("No such demo."), StatusCodes.Status404NotFound)
        : Results.File(path, "application/octet-stream", Path.GetFileName(path));
});

// For the unit and for whatever ends up watching it: it proves the database can be read, which is the
// one thing that can be true of the process and false of the deployment.
app.MapGet("/healthz", () =>
{
    try
    {
        using var db = StatsQueries.OpenReadOnly(settings.DatabasePath);
        StatsQueries.RecentMatches(db, 1);
        return Results.Text("ok\n");
    }
    catch (Exception e)
    {
        return Results.Text($"database unreadable: {e.Message}\n", "text/plain",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static IResult Html(string html, int status = StatusCodes.Status200OK) =>
    Results.Text(html, "text/html; charset=utf-8", statusCode: status);

// Where the database and the demos are. Environment variables rather than appsettings.json, because the
// deployment is a systemd unit and this way there is one place the paths are written down.
internal sealed record Settings(string DatabasePath, DemoStore Demos)
{
    public static Settings FromEnvironment()
    {
        string? db = Environment.GetEnvironmentVariable("GPP_DB");
        if (string.IsNullOrWhiteSpace(db))
            throw new InvalidOperationException("GPP_DB must point at the plugin's gpp.db.");

        return new Settings(db, new DemoStore(Environment.GetEnvironmentVariable("GPP_DEMOS")));
    }
}

// The demo directory, if there is one. Nothing in the database records a demo's filename, so a match's
// demos are found by name: the plugin's default demo_name_format puts the map and both team names in it.
internal sealed class DemoStore(string? directory)
{
    private static readonly string[] Extensions = [".dem", ".bz2", ".zip"];

    public bool Configured { get; } = !string.IsNullOrWhiteSpace(directory);

    private string? Root { get; } = string.IsNullOrWhiteSpace(directory)
        ? null
        : Path.GetFullPath(directory);

    public IReadOnlyList<(string Name, long Bytes, DateTime Modified)> List()
    {
        if (Root is null || !Directory.Exists(Root)) return [];
        return new DirectoryInfo(Root).EnumerateFiles()
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    public IReadOnlyList<string> ForMatch(StatsQueries.MatchRow match)
    {
        string map = match.Map;
        string team1 = Pages.Team(match.Team1);
        string team2 = Pages.Team(match.Team2);
        return List()
            .Where(d => d.Name.Contains(map, StringComparison.OrdinalIgnoreCase)
                     && d.Name.Contains(team1, StringComparison.OrdinalIgnoreCase)
                     && d.Name.Contains(team2, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToList();
    }

    // A name from a URL is never joined to the root directly: it is matched against what is actually on
    // disk, so "../../etc/passwd" cannot name a file this never listed.
    public string? Resolve(string name)
    {
        if (Root is null) return null;
        var match = List().FirstOrDefault(d => d.Name == name);
        return match.Name is null ? null : Path.Combine(Root, match.Name);
    }
}
