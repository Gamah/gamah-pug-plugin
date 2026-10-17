using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Gpp;

// The optional half of the project. With discord.base_url empty this file does nothing at all: no
// requests, no timers, and !link-discord tells the player the server has no bot. Everything the pug does
// works the same either way, which is the point - the bot is a convenience bolted to the side, never
// something the draft waits on.
//
// Three calls, all fire-and-forget:
//
//   POST /link/start   {steam_id, name}            -> {code, expires_in}   who is asking to link
//   POST /match/teams  {team1:[...], team2:[...]}  -> {moved, skipped}     move the rosters
//   POST /match/end    {}                          -> {moved}              everyone back to warmup
//
// The game thread never blocks on any of them. HttpClient work happens on the thread pool and the reply
// comes back through Server.NextFrame, because touching a player off the main thread is how a plugin
// takes the server down with it.
public partial class Gpp
{
    private static readonly HttpClient Http = new();

    [ConsoleCommand("css_link-discord", "Get a code to link your Steam account to Discord")]
    public void OnLinkDiscordCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        AfterTick(() => StartDiscordLink(player));
    }

    private void StartDiscordLink(CCSPlayerController player)
    {
        if (!Config.Discord.Enabled)
        {
            player.PrintToChat(Prefix() + "This server has no Discord bot set up.");
            return;
        }

        ulong steamId = player.SteamID;
        string name = player.PlayerName;
        player.PrintToChat(Prefix() + "Asking the bot for a code...");

        var body = new JsonObject { ["steam_id"] = steamId.ToString(), ["name"] = name };
        PostToBot("/link/start", body, reply =>
        {
            // Back on the main thread, and the player may well have left while we waited.
            var who = PlayerBySteamId(steamId);
            if (who == null || !who.IsValid) return;

            string? code = reply?["code"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(code))
            {
                who.PrintToChat(Prefix() + "Could not reach the Discord bot - try again in a minute.");
                return;
            }

            int minutes = Math.Max(1, (reply?["expires_in"]?.GetValue<int>() ?? 600) / 60);
            who.PrintToChat(Prefix() + $"In Discord, run {Colorize("{lime}")}/link-discord {code}"
                + $"{Colorize("{default}")} - the code lasts {minutes} minutes.");
        });
    }

    // Called at the handoff, once the rosters are final. Sends SteamIDs only: the bot owns the mapping to
    // Discord accounts, and the plugin deliberately knows nothing about who is on which Discord.
    private void MoveDiscordTeams()
    {
        if (!Config.Discord.Enabled || !Config.Discord.MoveOnHandoff) return;

        var team1 = new JsonArray();
        foreach (var id in _team1.Where(id => !IsBotId(id))) team1.Add(id.ToString());
        var team2 = new JsonArray();
        foreach (var id in _team2.Where(id => !IsBotId(id))) team2.Add(id.ToString());
        if (team1.Count == 0 && team2.Count == 0) return;

        var body = new JsonObject { ["team1"] = team1, ["team2"] = team2 };
        PostToBot("/match/teams", body, reply =>
        {
            int moved = reply?["moved"]?.GetValue<int>() ?? 0;
            // Players who are not linked, or not in voice, are skipped without comment - they said they
            // did not want nagging. The count is logged for whoever is debugging the bot, not announced.
            int skipped = reply?["skipped"]?.GetValue<int>() ?? 0;
            Logger.LogInformation("Discord: moved {Moved} player(s), skipped {Skipped}.", moved, skipped);
        });
    }

    private void ReturnDiscordTeams()
    {
        if (!Config.Discord.Enabled || !Config.Discord.ReturnOnMatchEnd) return;

        PostToBot("/match/end", new JsonObject(), reply =>
        {
            Logger.LogInformation("Discord: returned {Moved} player(s) to warmup.",
                reply?["moved"]?.GetValue<int>() ?? 0);
        });
    }

    // onReply runs on the main thread, with null if anything at all went wrong. Nothing here throws into
    // the game: a bot that is down, slow or talking nonsense is a log line, never a broken pug.
    private void PostToBot(string path, JsonObject body, Action<JsonObject?> onReply)
    {
        string url = Config.Discord.BaseUrl.TrimEnd('/') + path;
        string token = Config.Discord.Token;
        int timeout = Math.Max(1, Config.Discord.TimeoutSeconds);
        string payload = body.ToJsonString();

        _ = Task.Run(async () =>
        {
            JsonObject? reply = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(token)) request.Headers.Add("X-GPP-Token", token);

                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                using var response = await Http.SendAsync(request, cancel.Token);
                string text = await response.Content.ReadAsStringAsync(cancel.Token);

                if (response.IsSuccessStatusCode)
                {
                    reply = JsonNode.Parse(text) as JsonObject;
                }
                else
                {
                    Logger.LogWarning("Discord bot {Path} returned {Status}: {Body}",
                        path, (int)response.StatusCode, Trim(text));
                }
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Discord bot {Path} failed: {Message}", path, exception.Message);
            }

            Server.NextFrame(() => onReply(reply));
        });
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200];
}
