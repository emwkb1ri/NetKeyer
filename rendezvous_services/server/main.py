from __future__ import annotations

import asyncio
import contextlib
import ipaddress
import logging
import os

from fastapi import FastAPI, HTTPException, Request, WebSocket

from service_version import load_version_block

from .port_mapping import RendezvousPortMapper
from .state import RendezvousState
from .websocket_handlers import handle_client_ws, handle_host_ws


LOGGER = logging.getLogger("netkeyer.rendezvous")

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
HEALTH_ACCESS_MODE = os.getenv("RENDEZVOUS_HEALTH_ACCESS_MODE", "private").strip().lower()
HEALTH_ALLOWED_CIDRS = [
    value.strip()
    for value in os.getenv(
        "RENDEZVOUS_HEALTH_ALLOWED_CIDRS",
        "127.0.0.1/32,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16",
    ).split(",")
    if value.strip()
]

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


def _ip_in_allowed_cidrs(ip_text: str, cidrs: list[str]) -> bool:
    try:
        ip_value = ipaddress.ip_address(ip_text)
    except ValueError:
        return False

    for cidr in cidrs:
        try:
            network = ipaddress.ip_network(cidr, strict=False)
        except ValueError:
            continue
        if ip_value in network:
            return True

    return False


def is_health_request_allowed(client_host: str | None, mode: str | None = None, allowed_cidrs: list[str] | None = None) -> bool:
    effective_mode = (mode or HEALTH_ACCESS_MODE or "private").strip().lower()

    if effective_mode == "public":
        return True

    if effective_mode == "disabled":
        return False

    if not client_host:
        return False

    if effective_mode == "private":
        try:
            ip_value = ipaddress.ip_address(client_host)
        except ValueError:
            return False
        return ip_value.is_loopback or ip_value.is_private

    if effective_mode == "cidr":
        cidr_list = allowed_cidrs or HEALTH_ALLOWED_CIDRS
        return _ip_in_allowed_cidrs(client_host, cidr_list)

    return False


async def _session_sweeper() -> None:
    while True:
        await asyncio.sleep(SWEEP_INTERVAL_SECONDS)
        await state.sweep_expired_sessions(ttl_seconds=SESSION_TTL_SECONDS)


@contextlib.asynccontextmanager
async def lifespan(_: FastAPI):
    LOGGER.info(
        "rendezvous starting services_version=%s protocol=%s tag=%s commit=%s built_at=%s",
        VERSION_INFO.get("services_version", ""),
        VERSION_INFO.get("protocol_version", ""),
        VERSION_INFO.get("build", {}).get("tag", ""),
        VERSION_INFO.get("build", {}).get("commit", ""),
        VERSION_INFO.get("build", {}).get("built_at_utc", ""),
    )
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

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


@app.get("/health")
async def health(request: Request) -> dict[str, object]:
    client_host = request.client.host if request.client else None
    if not is_health_request_allowed(client_host):
        raise HTTPException(status_code=403, detail="health endpoint is restricted")

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
