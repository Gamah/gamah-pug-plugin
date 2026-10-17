"""Moving people between voice channels, and nothing else.

The one rule everything here is built around: **a member who is not already connected to voice cannot
be moved.** Discord's Modify Guild Member only accepts channel_id "if they are connected to voice", so
anyone sitting in a text channel, or not in Discord at all, is simply skipped. There is no way to pull
somebody in, and pretending otherwise would mean a pug that waits for a move that can never happen.

Skips are silent by design. An unlinked player is not nagged, in game or in Discord.
"""

from __future__ import annotations

import logging

import discord

log = logging.getLogger(__name__)


class Mover:
    def __init__(self, client: discord.Client, guild_id: int, warmup_id: int) -> None:
        self._client = client
        self._guild_id = guild_id
        self._warmup_id = warmup_id
        # Who this bot moved for the current match, so the end of it can put them back. Deliberately
        # in memory and not in the database: if the bot restarts mid-match the right answer is to
        # leave everyone where they are, not to shuffle a room it has lost track of.
        self._moved: set[int] = set()

    @property
    def guild(self) -> discord.Guild | None:
        return self._client.get_guild(self._guild_id)

    async def move_many(self, discord_ids: list[int], channel_id: int, reason: str) -> tuple[int, int]:
        """Returns (moved, skipped). Never raises."""
        guild = self.guild
        if guild is None:
            log.warning("guild %s is not in the cache - is the bot in it?", self._guild_id)
            return 0, len(discord_ids)

        channel = guild.get_channel(channel_id)
        if not isinstance(channel, discord.VoiceChannel):
            log.error("channel %s is not a voice channel the bot can see", channel_id)
            return 0, len(discord_ids)

        moved = 0
        skipped = 0
        for discord_id in discord_ids:
            member = guild.get_member(discord_id)
            if member is None:
                log.debug("%s is not in the guild", discord_id)
                skipped += 1
                continue

            voice = member.voice
            if voice is None or voice.channel is None:
                log.debug("%s is not in a voice channel", member)
                skipped += 1
                continue

            # Warmup is allowed to be the same channel as one of the teams, so half the room is
            # already where it needs to be. That is a success, not a move.
            if voice.channel.id == channel.id:
                moved += 1
                continue

            try:
                await member.move_to(channel, reason=reason)
            except discord.Forbidden:
                log.error(
                    "no permission to move %s into %s - the bot needs MOVE_MEMBERS, plus CONNECT "
                    "and VIEW_CHANNEL on that channel",
                    member,
                    channel.name,
                )
                skipped += 1
                continue
            except discord.HTTPException as error:
                log.warning("could not move %s: %s", member, error)
                skipped += 1
                continue

            self._moved.add(discord_id)
            moved += 1

        return moved, skipped

    async def return_to_warmup(self, reason: str) -> int:
        """Put back everyone this bot moved for the match that just ended."""
        going_back = sorted(self._moved)
        self._moved.clear()
        if not going_back:
            return 0
        moved, _ = await self.move_many(going_back, self._warmup_id, reason)
        # move_many re-recorded them on the way back; the match is over, so forget them again.
        self._moved.clear()
        return moved

    def forget(self) -> None:
        self._moved.clear()
