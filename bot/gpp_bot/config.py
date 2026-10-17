"""Reads config.toml and refuses to start on anything obviously wrong.

Everything is validated up front rather than at the moment it is first used: a missing channel ID that
only bites at the handoff means it bites in the middle of a pug, with ten people waiting.
"""

from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path


class ConfigError(Exception):
    pass


@dataclass(frozen=True)
class Config:
    discord_token: str
    guild_id: int
    warmup_channel_id: int
    team_a_channel_id: int
    team_b_channel_id: int
    http_host: str
    http_port: int
    http_token: str
    code_ttl_seconds: int
    database: Path

    @property
    def channel_ids(self) -> set[int]:
        return {self.warmup_channel_id, self.team_a_channel_id, self.team_b_channel_id}


def _require(table: dict, section: str, key: str) -> str:
    value = str(table.get(key, "")).strip()
    if not value:
        raise ConfigError(f"[{section}] {key} is not set")
    return value


def _require_id(table: dict, section: str, key: str) -> int:
    value = _require(table, section, key)
    if not value.isdigit():
        raise ConfigError(f"[{section}] {key} must be a numeric Discord ID, got {value!r}")
    return int(value)


def load(path: Path) -> Config:
    if not path.exists():
        raise ConfigError(f"{path} does not exist - copy config.example.toml and fill it in")

    with path.open("rb") as handle:
        raw = tomllib.load(handle)

    discord = raw.get("discord", {})
    http = raw.get("http", {})
    link = raw.get("link", {})

    config = Config(
        discord_token=_require(discord, "discord", "token"),
        guild_id=_require_id(discord, "discord", "guild_id"),
        warmup_channel_id=_require_id(discord, "discord", "warmup_channel_id"),
        team_a_channel_id=_require_id(discord, "discord", "team_a_channel_id"),
        team_b_channel_id=_require_id(discord, "discord", "team_b_channel_id"),
        http_host=str(http.get("host", "127.0.0.1")),
        http_port=int(http.get("port", 8787)),
        http_token=_require(http, "http", "token"),
        code_ttl_seconds=int(link.get("code_ttl_seconds", 600)),
        database=(path.parent / str(link.get("database", "links.sqlite3"))).resolve(),
    )

    # Warmup sharing a channel with one of the teams is a supported setup - it halves how many people
    # have to move. The two team channels being the same is not: there would be no teams.
    if config.team_a_channel_id == config.team_b_channel_id:
        raise ConfigError("team_a_channel_id and team_b_channel_id must be different channels")

    return config
