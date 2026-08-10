from __future__ import annotations

import asyncio
import contextlib
import os

from fastapi import FastAPI, WebSocket

from service_version import load_version_block

from .port_mapping import RendezvousPortMapper
from .state import RendezvousState
from .websocket_handlers import handle_client_ws, handle_host_ws


state = RendezvousState()

RELAY_HOST = os.getenv("RENDEZVOUS_RELAY_HOST", "relay")
RELAY_PORT = int(os.getenv("RENDEZVOUS_RELAY_PORT", "49921"))
SWEEP_INTERVAL_SECONDS = int(os.getenv("RENDEZVOUS_SWEEP_INTERVAL_SECONDS", "5"))
SESSION_TTL_SECONDS = int(os.getenv("RENDEZVOUS_SESSION_TTL_SECONDS", "30"))
CONTROL_PORT = int(os.getenv("RENDEZVOUS_CONTROL_PORT", "49920"))
PORTMAP_ENABLED = os.getenv("RENDEZVOUS_ENABLE_PORT_MAP", "true").strip().lower() in {"1", "true", "yes", "on"}
ENABLE_NGINX_PORT_MAP = os.getenv("RENDEZVOUS_ENABLE_NGINX_PORT_MAP", "false").strip().lower() in {"1", "true", "yes", "on"}
NGINX_PORT = int(os.getenv("RENDEZVOUS_NGINX_PORT", "49922"))
PORTMAP_HOST_IPS = [ip.strip() for ip in os.getenv("RENDEZVOUS_PORTMAP_HOST_IPS", "").split(",") if ip.strip()]
PORTMAP_INTERNAL_IP = os.getenv("RENDEZVOUS_PORTMAP_INTERNAL_IP", "").strip()
NATPMP_GATEWAY_IP = os.getenv("RENDEZVOUS_NATPMP_GATEWAY_IP", "").strip()
VERSION_INFO = load_version_block(component="rendezvous")

PORT_MAPPER = RendezvousPortMapper(
    enabled=PORTMAP_ENABLED,
    mappings=[
        ("rendezvous_control", CONTROL_PORT, True),
        ("relay", RELAY_PORT, True),
        ("nginx_relay_proxy", NGINX_PORT, ENABLE_NGINX_PORT_MAP),
    ],
    known_host_ips=PORTMAP_HOST_IPS,
    upnp_internal_ip=PORTMAP_INTERNAL_IP,
    natpmp_gateway_ip=NATPMP_GATEWAY_IP,
)


async def _session_sweeper() -> None:
    while True:
        await asyncio.sleep(SWEEP_INTERVAL_SECONDS)
        await state.sweep_expired_sessions(ttl_seconds=SESSION_TTL_SECONDS)


@contextlib.asynccontextmanager
async def lifespan(_: FastAPI):
    await asyncio.to_thread(PORT_MAPPER.run_mapping)
    sweeper = asyncio.create_task(_session_sweeper())
    try:
        yield
    finally:
        sweeper.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await sweeper
        await asyncio.to_thread(PORT_MAPPER.clear_mappings)


app = FastAPI(title="NetKeyer Rendezvous Server", version=str(VERSION_INFO["services_version"]), lifespan=lifespan)


@app.get("/health")
async def health() -> dict[str, object]:
    statistics = await state.get_statistics_snapshot()
    return {
        "status": "ok",
        "relay_host": RELAY_HOST,
        "relay_port": RELAY_PORT,
        "control_port": CONTROL_PORT,
        "version": VERSION_INFO,
        "port_mapping": PORT_MAPPER.snapshot.to_dict(),
        "statistics": statistics,
    }


@app.websocket("/ws/host")
async def ws_host(websocket: WebSocket) -> None:
    await handle_host_ws(state, websocket, relay_host=RELAY_HOST, relay_port=RELAY_PORT)


@app.websocket("/ws/client")
async def ws_client(websocket: WebSocket) -> None:
    await handle_client_ws(state, websocket, relay_host=RELAY_HOST, relay_port=RELAY_PORT)
