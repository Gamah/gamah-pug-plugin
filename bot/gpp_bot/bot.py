"""The Discord client: one slash command to redeem a code, one to undo it.

Intents: GUILD_VOICE_STATES for who is in which voice channel, and GUILD_MEMBERS to resolve a member
by ID. GUILD_MEMBERS is a privileged intent - tick "Server Members Intent" in the developer portal or
the bot will not start. Neither message content nor presences are needed, and both stay off.
"""

from __future__ import annotations

import logging

import discord
from discord import app_commands

from .config import Config
from .store import Store

log = logging.getLogger(__name__)


def build_client(config: Config, store: Store) -> discord.Client:
    intents = discord.Intents.none()
    intents.guilds = True
    intents.voice_states = True
    intents.members = True

    client = discord.Client(intents=intents)
    tree = app_commands.CommandTree(client)
    guild = discord.Object(id=config.guild_id)

    @client.event
    async def on_ready() -> None:
        # Guild-scoped so the commands appear immediately; global ones take up to an hour to show up.
        tree.copy_global_to(guild=guild)
        await tree.sync(guild=guild)
        log.info("connected as %s", client.user)
        _warn_about_permissions(client, config)

    @tree.command(name="link-discord", description="Link your Steam account using the code from the game server")
    @app_commands.describe(code="The code !link-discord gave you in game")
    async def link_discord(interaction: discord.Interaction, code: str) -> None:
        linked = store.redeem(code, str(interaction.user.id))
        if linked is None:
            await interaction.response.send_message(
                "That code is wrong or has expired. Type `!link-discord` in game for a new one.",
                ephemeral=True,
            )
            return

        log.info("linked steam %s to discord %s", linked.steam_id, interaction.user.id)
        await interaction.response.send_message(
            f"Linked to Steam ID `{linked.steam_id}`. You will be moved with your team from now on.",
            ephemeral=True,
        )

    @tree.command(name="unlink-discord", description="Forget the Steam account linked to you")
    async def unlink_discord(interaction: discord.Interaction) -> None:
        removed = store.unlink_discord(str(interaction.user.id))
        await interaction.response.send_message(
            "Unlinked." if removed else "You were not linked to anything.", ephemeral=True
        )

    @tree.command(name="whoami", description="Show which Steam account you are linked to")
    async def whoami(interaction: discord.Interaction) -> None:
        steam_id = store.steam_id_for_discord(str(interaction.user.id))
        await interaction.response.send_message(
            f"Steam ID `{steam_id}`." if steam_id else "You are not linked. Type `!link-discord` in game.",
            ephemeral=True,
        )

    return client


def _warn_about_permissions(client: discord.Client, config: Config) -> None:
    """Say at startup what will fail at the handoff, rather than at the handoff.

    Discord's Modify Guild Member needs MOVE_MEMBERS *and* the right to connect to the destination, so
    a bot with the global permission but no access to one channel half-works in a way that is painful
    to diagnose mid-pug.
    """
    guild = client.get_guild(config.guild_id)
    if guild is None:
        log.error("not in guild %s - invite the bot first", config.guild_id)
        return

    me = guild.me
    if not me.guild_permissions.move_members:
        log.error("missing MOVE_MEMBERS in %s - nobody will be moved", guild.name)

    for label, channel_id in (
        ("warmup", config.warmup_channel_id),
        ("team A", config.team_a_channel_id),
        ("team B", config.team_b_channel_id),
    ):
        channel = guild.get_channel(channel_id)
        if not isinstance(channel, discord.VoiceChannel):
            log.error("%s channel %s is missing or is not a voice channel", label, channel_id)
            continue
        perms = channel.permissions_for(me)
        if not (perms.view_channel and perms.connect and perms.move_members):
            log.error(
                "need VIEW_CHANNEL, CONNECT and MOVE_MEMBERS on %s (%s) - moves into it will fail",
                channel.name,
                label,
            )
