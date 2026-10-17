"""The HTTP side: three endpoints the game server calls, and a health check.

Bind this to localhost unless the plugin is on another box. The shared token is the only thing standing
between this and anyone who can reach the port, and all it protects is "move people between voice
channels" - unpleasant rather than catastrophic, but not something to leave open to the internet.
"""

from __future__ import annotations

import hmac
import logging

from typing import TYPE_CHECKING

from aiohttp import web

from .config import Config
from .store import Store

if TYPE_CHECKING:  # the HTTP layer needs no Discord library to run, only to be type-checked
    from .mover import Mover

log = logging.getLogger(__name__)


def _authorised(request: web.Request, token: str) -> bool:
    # compare_digest, not ==, so the comparison does not leak the token's length or prefix by timing.
    return hmac.compare_digest(request.headers.get("X-GPP-Token", ""), token)


def _steam_ids(payload: object, key: str) -> list[str]:
    if not isinstance(payload, dict):
        return []
    raw = payload.get(key, [])
    if not isinstance(raw, list):
        return []
    return [str(item) for item in raw if str(item).isdigit()]


def build_app(config: Config, store: Store, mover: "Mover") -> web.Application:
    app = web.Application()

    @web.middleware
    async def auth(request: web.Request, handler):
        if request.path != "/health" and not _authorised(request, config.http_token):
            log.warning("refused %s from %s: bad or missing token", request.path, request.remote)
            return web.json_response({"error": "unauthorised"}, status=401)
        return await handler(request)

    app.middlewares.append(auth)

    async def health(_: web.Request) -> web.Response:
        guild = mover.guild
        return web.json_response(
            {
                "ok": guild is not None,
                "guild": guild.name if guild else None,
            }
        )

    async def link_start(request: web.Request) -> web.Response:
        payload = await request.json()
        steam_id = str(payload.get("steam_id", "")).strip()
        if not steam_id.isdigit():
            return web.json_response({"error": "steam_id must be a SteamID64"}, status=400)

        name = str(payload.get("name", ""))[:64]
        code = store.start_link(steam_id, name, config.code_ttl_seconds)
        log.info("link code issued for %s (%s)", steam_id, name)
        return web.json_response({"code": code, "expires_in": config.code_ttl_seconds})

    async def match_teams(request: web.Request) -> web.Response:
        payload = await request.json()
        team1 = _steam_ids(payload, "team1")
        team2 = _steam_ids(payload, "team2")

        # A fresh match: whatever the last one left recorded is no longer ours to put back.
        mover.forget()

        known = store.discord_ids_for(team1 + team2)
        moved_a, skipped_a = await mover.move_many(
            [known[s] for s in team1 if s in known], config.team_a_channel_id, "GPP: team A"
        )
        moved_b, skipped_b = await mover.move_many(
            [known[s] for s in team2 if s in known], config.team_b_channel_id, "GPP: team B"
        )

        unlinked = len(team1) + len(team2) - len(known)
        skipped = skipped_a + skipped_b + unlinked
        log.info(
            "match teams: moved %s, skipped %s (%s unlinked)", moved_a + moved_b, skipped, unlinked
        )
        return web.json_response({"moved": moved_a + moved_b, "skipped": skipped})

    async def match_end(_: web.Request) -> web.Response:
        moved = await mover.return_to_warmup("GPP: match over")
        log.info("match end: returned %s to warmup", moved)
        return web.json_response({"moved": moved})

    app.router.add_get("/health", health)
    app.router.add_post("/link/start", link_start)
    app.router.add_post("/match/teams", match_teams)
    app.router.add_post("/match/end", match_end)
    return app
