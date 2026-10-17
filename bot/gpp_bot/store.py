"""The SteamID -> Discord ID table, and the short-lived codes that populate it.

SQLite, one file, no server. The table is small (one row per player, forever) and the access pattern is
a handful of reads per pug, so anything heavier would be for show.

SteamIDs are stored as TEXT. They are 17-digit numbers that fit in an unsigned 64-bit integer but not a
signed one, and SQLite integers are signed - storing them as numbers is a rounding bug waiting to be
written. Everything in and out of here is a string.
"""

from __future__ import annotations

import secrets
import sqlite3
import time
from dataclasses import dataclass
from pathlib import Path

# No vowels, no 0/O, no 1/I/L: the code gets read off one screen and typed into another.
CODE_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
CODE_LENGTH = 6

SCHEMA = """
CREATE TABLE IF NOT EXISTS links (
    steam_id   TEXT PRIMARY KEY,
    discord_id TEXT NOT NULL,
    name       TEXT NOT NULL DEFAULT '',
    linked_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS links_discord ON links (discord_id);

CREATE TABLE IF NOT EXISTS codes (
    code       TEXT PRIMARY KEY,
    steam_id   TEXT NOT NULL,
    name       TEXT NOT NULL DEFAULT '',
    expires_at INTEGER NOT NULL
);
"""


@dataclass(frozen=True)
class PendingLink:
    code: str
    steam_id: str
    name: str


class Store:
    def __init__(self, path: Path) -> None:
        self._db = sqlite3.connect(path, check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        self._db.executescript(SCHEMA)
        self._db.commit()

    def close(self) -> None:
        self._db.close()

    # --- codes -----------------------------------------------------------------

    def start_link(self, steam_id: str, name: str, ttl_seconds: int) -> str:
        """Issue a code for this SteamID, replacing any code it already had.

        One outstanding code per player: typing !link-discord twice should not leave the first code
        working, or a player who shared a code by mistake has no way to revoke it.
        """
        self._db.execute("DELETE FROM codes WHERE steam_id = ?", (steam_id,))
        code = "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))
        self._db.execute(
            "INSERT INTO codes (code, steam_id, name, expires_at) VALUES (?, ?, ?, ?)",
            (code, steam_id, name, int(time.time()) + ttl_seconds),
        )
        self._db.commit()
        return code

    def redeem(self, code: str, discord_id: str) -> PendingLink | None:
        """Spend a code and write the link. Returns None if the code is unknown or expired."""
        self._purge_expired()
        row = self._db.execute(
            "SELECT code, steam_id, name FROM codes WHERE code = ?", (code.strip().upper(),)
        ).fetchone()
        if row is None:
            return None

        # Re-linking is a replace, not an error: people change Discord accounts, and a player who ran
        # !link-discord again clearly meant the new one to win.
        self._db.execute("DELETE FROM codes WHERE code = ?", (row["code"],))
        self._db.execute(
            "INSERT INTO links (steam_id, discord_id, name, linked_at) VALUES (?, ?, ?, ?) "
            "ON CONFLICT(steam_id) DO UPDATE SET discord_id = excluded.discord_id, "
            "name = excluded.name, linked_at = excluded.linked_at",
            (row["steam_id"], discord_id, row["name"], int(time.time())),
        )
        self._db.commit()
        return PendingLink(code=row["code"], steam_id=row["steam_id"], name=row["name"])

    def _purge_expired(self) -> None:
        self._db.execute("DELETE FROM codes WHERE expires_at < ?", (int(time.time()),))
        self._db.commit()

    # --- links -----------------------------------------------------------------

    def discord_ids_for(self, steam_ids: list[str]) -> dict[str, int]:
        """SteamID -> Discord ID for whichever of those SteamIDs are linked. Unlinked ones are absent."""
        if not steam_ids:
            return {}
        marks = ",".join("?" * len(steam_ids))
        rows = self._db.execute(
            f"SELECT steam_id, discord_id FROM links WHERE steam_id IN ({marks})", steam_ids
        ).fetchall()
        return {row["steam_id"]: int(row["discord_id"]) for row in rows}

    def unlink_discord(self, discord_id: str) -> int:
        cursor = self._db.execute("DELETE FROM links WHERE discord_id = ?", (discord_id,))
        self._db.commit()
        return cursor.rowcount

    def steam_id_for_discord(self, discord_id: str) -> str | None:
        row = self._db.execute(
            "SELECT steam_id FROM links WHERE discord_id = ?", (discord_id,)
        ).fetchone()
        return row["steam_id"] if row else None
