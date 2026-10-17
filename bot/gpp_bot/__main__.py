"""Runs the Discord client and the HTTP API in one event loop.

Both halves have to share a loop: the HTTP handlers call straight into discord.py to move people, and
bouncing that across threads would buy nothing but a class of bug.
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import signal
from pathlib import Path

from aiohttp import web

from . import config as config_module
from .api import build_app
from .bot import build_client
from .mover import Mover
from .store import Store

log = logging.getLogger("gpp_bot")


async def run(config: config_module.Config) -> None:
    store = Store(config.database)
    client = build_client(config, store)
    mover = Mover(client, config.guild_id, config.warmup_channel_id)

    runner = web.AppRunner(build_app(config, store, mover))
    await runner.setup()
    site = web.TCPSite(runner, config.http_host, config.http_port)
    await site.start()
    log.info("listening on http://%s:%s", config.http_host, config.http_port)

    connect: asyncio.Task | None = None
    stopping = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        # add_signal_handler beats a KeyboardInterrupt handler: it unwinds through the same path for
        # a Ctrl-C and for systemd stopping the unit, so there is one shutdown to get right.
        loop.add_signal_handler(sig, stopping.set)

    try:
        await client.login(config.discord_token)
        connect = asyncio.create_task(client.connect())
        await stopping.wait()
    finally:
        log.info("shutting down")
        await client.close()
        await runner.cleanup()
        store.close()
        if connect is not None:
            connect.cancel()


def main() -> int:
    parser = argparse.ArgumentParser(prog="gpp-bot", description=__doc__)
    parser.add_argument(
        "-c", "--config", type=Path, default=Path("config.toml"), help="path to config.toml"
    )
    parser.add_argument("-v", "--verbose", action="store_true", help="debug logging")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
    )

    try:
        config = config_module.load(args.config)
    except config_module.ConfigError as error:
        log.error("%s", error)
        return 2

    try:
        asyncio.run(run(config))
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
