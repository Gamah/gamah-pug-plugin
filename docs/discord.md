# The Discord bot

Optional. It does one useful thing: when the draft is done and the match starts, it moves each team into
its own voice channel, and puts everyone back when the match ends.

Everything else here exists to solve the one hard problem underneath that — **knowing which Discord
account belongs to which Steam account.**

## Why linking is needed at all

A bot cannot look this up. Discord's `GET /users/@me/connections` only ever returns the *authorising*
user's own linked accounts, and it needs the `connections` OAuth2 scope; there is no bot-token route to
"who is this Discord user on Steam". Names are no help either — Discord names and Steam names rarely
match, and matching them by guesswork would eventually drag the wrong person into the wrong team.

So each player links once, by hand, and it sticks:

1. In game: `!link-discord`. The plugin asks the bot for a code and prints it to that player only.
2. In Discord: `/link-discord ABC123`. The bot writes `SteamID64 -> Discord user ID` to its database.

Codes are six characters from an alphabet with no vowels and no `0/O/1/I/L`, last ten minutes by
default, work once, and a second `!link-discord` revokes the first. The reply in Discord is ephemeral,
so nobody else sees who linked what.

`/unlink-discord` forgets it. `/whoami` shows what you are linked to.

## What the bot can and cannot do

**It can only move someone who is already in a voice channel.** Discord's Modify Guild Member takes
`channel_id` "if they are connected to voice" — there is no way to pull a player in from nothing. In
practice that means the room gathers in the warmup channel and gets fanned out from there.

Players who are not linked, or not in voice, are **skipped silently**. No DM, no nag in game chat. The
count goes to the server log for whoever is debugging, and that is all.

## Channels

Three channel IDs in the bot config: warmup, team A, team B. Team A is GPP's team1 (CT), team B is
team2 (T).

They may overlap. Setting `warmup_channel_id` to the same ID as `team_a_channel_id` means everyone
gathers in what is also Team A's channel, so only Team B moves at the handoff — half the disruption,
and the usual way to run it. The bot treats "already in the target channel" as a success and makes no
API call. Setting the two team channels to the same ID is refused at startup, because then there are no
teams.

The bot never creates, renames or deletes a channel. You make the three channels yourself and give it
their IDs. That is deliberate: creating channels would need `MANAGE_CHANNELS`, which carries a 2FA
requirement on guilds with server-wide 2FA, and a bug in a bot that can create channels makes a mess
that a bug in a bot that can only move people does not.

## Permissions and intents

The bot needs, on each of the three channels:

| Permission | Bit | Why |
| --- | --- | --- |
| `MOVE_MEMBERS` | `1 << 24` | "Allows for moving of members between voice channels" |
| `CONNECT` | `1 << 20` | Discord requires the caller to be able to connect to the destination |
| `VIEW_CHANNEL` | `1 << 10` | To see the channel at all |

Gateway intents: `GUILD_VOICE_STATES` (`1 << 7`, not privileged) to know who is in voice, and
`GUILD_MEMBERS` (**privileged** — tick "Server Members Intent" in the developer portal) to resolve a
member by ID. Message content and presences are not used and stay off.

The bot logs a specific error at startup for each of these that is missing, rather than failing in the
middle of a pug.

## Wiring it up

In the bot's `config.toml` (copy `config.example.toml`):

```toml
[discord]
token = "..."              # bot token
guild_id = "..."           # right-click the server -> Copy Server ID
warmup_channel_id = "..."
team_a_channel_id = "..."  # may be the same as warmup
team_b_channel_id = "..."

[http]
host = "127.0.0.1"
port = 8787
token = "a long random string"
```

In the plugin's `Gpp.json`:

```json
"discord": {
  "base_url": "http://127.0.0.1:8787",
  "token": "the same long random string",
  "move_on_handoff": true,
  "return_on_match_end": true,
  "timeout_seconds": 5
}
```

`base_url` empty disables the whole thing.

Run it:

```
cd bot
python3 -m venv .venv && .venv/bin/pip install -e .
.venv/bin/gpp-bot -c config.toml
```

Check it is up: `curl localhost:8787/health` — no token needed for that one route.

## The contract between the two

Three POSTs, all from the plugin to the bot, all authenticated with an `X-GPP-Token` header, all
fire-and-forget. **The game thread never waits on any of them**, and a bot that is down, slow or
talking nonsense produces a log line and nothing else — never a stuck pug.

| Route | Body | Reply |
| --- | --- | --- |
| `POST /link/start` | `{"steam_id": "765...", "name": "Tani Rani"}` | `{"code": "ABC123", "expires_in": 600}` |
| `POST /match/teams` | `{"team1": ["765..."], "team2": ["765..."]}` | `{"moved": 8, "skipped": 2}` |
| `POST /match/end` | `{}` | `{"moved": 8}` |
| `GET /health` | — | `{"ok": true, "guild": "..."}` (no token) |

SteamIDs are **strings everywhere**. A SteamID64 is larger than 2^53, so it does not survive a JSON
number in any JavaScript consumer, and it overflows a signed 64-bit SQLite integer column.

The plugin sends SteamIDs and nothing else. It never learns anyone's Discord identity; that mapping
lives only in the bot's database.

## Security

The shared token is the only thing in front of the HTTP API. What it protects is "move people between
voice channels" — annoying rather than catastrophic — but bind to `127.0.0.1` anyway unless the plugin
is on another machine, in which case put it behind a tunnel or a firewall rule rather than on the open
internet. Token comparison uses `hmac.compare_digest`.

`config.toml` holds two secrets (the bot token and the shared token) and is gitignored.
