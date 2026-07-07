from __future__ import annotations

import asyncio
import contextlib
import os

from fastapi import FastAPI, WebSocket

from .state import RendezvousState
from .websocket_handlers import handle_client_ws, handle_host_ws


state = RendezvousState()

RELAY_HOST = os.getenv("RENDEZVOUS_RELAY_HOST", "relay")
RELAY_PORT = int(os.getenv("RENDEZVOUS_RELAY_PORT", "49921"))
SWEEP_INTERVAL_SECONDS = int(os.getenv("RENDEZVOUS_SWEEP_INTERVAL_SECONDS", "5"))
SESSION_TTL_SECONDS = int(os.getenv("RENDEZVOUS_SESSION_TTL_SECONDS", "30"))


async def _session_sweeper() -> None:
    while True:
        await asyncio.sleep(SWEEP_INTERVAL_SECONDS)
        await state.sweep_expired_sessions(ttl_seconds=SESSION_TTL_SECONDS)


@contextlib.asynccontextmanager
async def lifespan(_: FastAPI):
    sweeper = asyncio.create_task(_session_sweeper())
    try:
        yield
    finally:
        sweeper.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await sweeper


app = FastAPI(title="NetKeyer Rendezvous Server", version="0.1.0", lifespan=lifespan)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.websocket("/ws/host")
async def ws_host(websocket: WebSocket) -> None:
    await handle_host_ws(state, websocket, relay_host=RELAY_HOST, relay_port=RELAY_PORT)


@app.websocket("/ws/client")
async def ws_client(websocket: WebSocket) -> None:
    await handle_client_ws(state, websocket, relay_host=RELAY_HOST, relay_port=RELAY_PORT)
