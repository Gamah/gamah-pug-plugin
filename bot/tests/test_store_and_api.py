"""Offline tests for the halves that do not need Discord: the link table and the HTTP API.

The Discord client is replaced by a stub Mover, so these run anywhere with no token and no guild.
What they are actually guarding:

  - SteamID64s survive the round trip as exact strings (they overflow a signed 64-bit integer, which
    is what a naive integer column would give them).
  - A code works once, and only for the player it was issued to.
  - The API refuses a wrong or missing token.
  - Warmup being the same channel as a team channel counts as moved, not skipped.

Run: python3 -m pytest bot/tests -q     (needs aiohttp; pytest-aiohttp not required)
"""

from __future__ import annotations

import asyncio
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp.test_utils import TestClient, TestServer  # noqa: E402

from gpp_bot.api import build_app  # noqa: E402
from gpp_bot.config import Config  # noqa: E402
from gpp_bot.store import Store  # noqa: E402

# Shaped like a SteamID64 but above the allocated range, so they belong to nobody. The size is the
# point: 76561199999999999 > 2**53, so these fail a round trip through a JSON number in any JavaScript
# consumer - another reason everything on the wire is a string.
STEAM_A = "76561199999999991"
STEAM_B = "76561199999999992"

WARMUP = 1001
TEAM_A = 1001  # deliberately the same as warmup: the supported "only half the room moves" setup
TEAM_B = 1002


class FakeGuild:
    name = "test guild"


class FakeMover:
    """Records calls instead of talking to Discord."""

    def __init__(self) -> None:
        self.calls: list[tuple[list[int], int]] = []
        self.forgotten = 0
        self.returned = 0
        self.guild = FakeGuild()

    async def move_many(self, discord_ids, channel_id, reason):
        self.calls.append((list(discord_ids), channel_id))
        return len(discord_ids), 0

    async def return_to_warmup(self, reason):
        self.returned += 1
        return 3

    def forget(self) -> None:
        self.forgotten += 1


def make_config(db: Path) -> Config:
    return Config(
        discord_token="x",
        guild_id=1,
        warmup_channel_id=WARMUP,
        team_a_channel_id=TEAM_A,
        team_b_channel_id=TEAM_B,
        http_host="127.0.0.1",
        http_port=0,
        http_token="sekrit",
        code_ttl_seconds=600,
        database=db,
    )


class StoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.dir.name) / "links.sqlite3")

    def tearDown(self) -> None:
        self.store.close()
        self.dir.cleanup()

    def test_steam_id_survives_exactly(self) -> None:
        code = self.store.start_link(STEAM_A, "Player One", 600)
        linked = self.store.redeem(code, "4242")
        assert linked is not None
        self.assertEqual(linked.steam_id, STEAM_A)
        self.assertEqual(self.store.discord_ids_for([STEAM_A]), {STEAM_A: 4242})

    def test_code_is_single_use(self) -> None:
        code = self.store.start_link(STEAM_A, "x", 600)
        self.assertIsNotNone(self.store.redeem(code, "1"))
        self.assertIsNone(self.store.redeem(code, "2"))

    def test_second_code_revokes_the_first(self) -> None:
        first = self.store.start_link(STEAM_A, "x", 600)
        self.store.start_link(STEAM_A, "x", 600)
        self.assertIsNone(self.store.redeem(first, "1"))

    def test_expired_code_is_refused(self) -> None:
        code = self.store.start_link(STEAM_A, "x", -1)
        self.assertIsNone(self.store.redeem(code, "1"))

    def test_relink_replaces(self) -> None:
        self.store.redeem(self.store.start_link(STEAM_A, "x", 600), "111")
        self.store.redeem(self.store.start_link(STEAM_A, "x", 600), "222")
        self.assertEqual(self.store.discord_ids_for([STEAM_A]), {STEAM_A: 222})

    def test_unlinked_players_are_absent_not_zero(self) -> None:
        self.store.redeem(self.store.start_link(STEAM_A, "x", 600), "111")
        found = self.store.discord_ids_for([STEAM_A, STEAM_B])
        self.assertEqual(found, {STEAM_A: 111})
        self.assertNotIn(STEAM_B, found)

    def test_code_alphabet_is_unambiguous(self) -> None:
        code = self.store.start_link(STEAM_A, "x", 600)
        self.assertEqual(len(code), 6)
        self.assertFalse(set(code) & set("OIL01"))


class ApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.dir = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.dir.name) / "links.sqlite3")
        self.config = make_config(Path(self.dir.name) / "links.sqlite3")
        self.mover = FakeMover()
        self.client = TestClient(TestServer(build_app(self.config, self.store, self.mover)))
        await self.client.start_server()

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.store.close()
        self.dir.cleanup()

    def auth(self, token: str = "sekrit") -> dict[str, str]:
        return {"X-GPP-Token": token}

    async def test_health_needs_no_token(self) -> None:
        response = await self.client.get("/health")
        self.assertEqual(response.status, 200)

    async def test_wrong_token_is_refused(self) -> None:
        response = await self.client.post("/match/end", headers=self.auth("nope"), json={})
        self.assertEqual(response.status, 401)

    async def test_missing_token_is_refused(self) -> None:
        response = await self.client.post("/match/end", json={})
        self.assertEqual(response.status, 401)

    async def test_link_start_returns_a_code(self) -> None:
        response = await self.client.post(
            "/link/start", headers=self.auth(), json={"steam_id": STEAM_A, "name": "Player One"}
        )
        self.assertEqual(response.status, 200)
        body = await response.json()
        self.assertEqual(len(body["code"]), 6)
        self.assertEqual(body["expires_in"], 600)

    async def test_link_start_rejects_rubbish(self) -> None:
        response = await self.client.post(
            "/link/start", headers=self.auth(), json={"steam_id": "not-a-steamid"}
        )
        self.assertEqual(response.status, 400)

    async def test_teams_only_move_linked_players(self) -> None:
        self.store.redeem(self.store.start_link(STEAM_A, "a", 600), "111")
        response = await self.client.post(
            "/match/teams", headers=self.auth(), json={"team1": [STEAM_A], "team2": [STEAM_B]}
        )
        body = await response.json()

        # STEAM_B is unlinked, so it is skipped without the bot ever asking Discord about it.
        self.assertEqual(body["moved"], 1)
        self.assertEqual(body["skipped"], 1)
        self.assertEqual(self.mover.calls, [([111], TEAM_A), ([], TEAM_B)])
        self.assertEqual(self.mover.forgotten, 1)

    async def test_teams_ignores_non_numeric_ids(self) -> None:
        response = await self.client.post(
            "/match/teams", headers=self.auth(), json={"team1": ["); DROP TABLE links;--"], "team2": []}
        )
        self.assertEqual((await response.json())["moved"], 0)
        # The table is still there.
        self.assertEqual(self.store.discord_ids_for([STEAM_A]), {})

    async def test_match_end_returns_everyone(self) -> None:
        response = await self.client.post("/match/end", headers=self.auth(), json={})
        self.assertEqual((await response.json())["moved"], 3)
        self.assertEqual(self.mover.returned, 1)


if __name__ == "__main__":
    unittest.main()
