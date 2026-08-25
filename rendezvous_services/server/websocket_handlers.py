from __future__ import annotations

import asyncio
from typing import Any

from fastapi import WebSocket, WebSocketDisconnect

from .models import (
    ClientRequestPortMapMessage,
    ConnectRequestMessage,
    ErrorMessage,
    HostEndpointMessage,
    HostPortMapResultMessage,
    HostListMessage,
    HostPunchResultMessage,
    HostSummary,
    IncomingClientMessage,
    ListHostsMessage,
    RequestPortMapMessage,
    RegisterClientMessage,
    RegisterHostMessage,
    StartPunchMessage,
    UseRelayMessage,
    is_validation_error,
    validate_client_inbound,
    validate_host_inbound,
)
from .state import RendezvousState


PUNCH_TIMEOUT_SECONDS = 2
PORT_MAP_TIMEOUT_SECONDS = 4
DEFAULT_RELAY_HOST = "relay"
DEFAULT_RELAY_PORT = 49921


def _model_dump(model: Any) -> dict[str, Any]:
    if hasattr(model, "model_dump"):
        return model.model_dump(exclude_none=True)
    return model.dict(exclude_none=True)


async def _send_model(ws: WebSocket, model: Any) -> None:
    await ws.send_json(_model_dump(model))


async def _send_error(ws: WebSocket, code: str, message: str, session_id: str | None = None) -> None:
    err = ErrorMessage(type="error", code=code, message=message, session_id=session_id)
    await _send_model(ws, err)


def _peer_endpoint(ws: WebSocket) -> tuple[str, int]:
    client = ws.client
    if client is None:
        return "0.0.0.0", 0
    return client.host, client.port


async def _send_relay_to_both(state: RendezvousState, session_id: str, relay_host: str, relay_port: int) -> None:
    session = await state.get_session(session_id)
    if not session:
        return

    host = await state.get_host(session.host_id)
    client = await state.get_client(session.client_id)

    advertised_relay_host = _resolve_advertised_relay_host(relay_host, host.ws if host else None, client.ws if client else None)

    msg = UseRelayMessage(
        type="use_relay",
        relay_host=advertised_relay_host,
        relay_port=relay_port,
        session_id=session_id,
    )

    if host:
        await _send_model(host.ws, msg)
    if client:
        await _send_model(client.ws, msg)


def _resolve_advertised_relay_host(configured_relay_host: str, host_ws: WebSocket | None, client_ws: WebSocket | None) -> str:
    candidate = (configured_relay_host or "").strip()
    if candidate and candidate.lower() != "relay":
        return candidate

    # Fall back to the host header used by external app websocket clients.
    for ws in (host_ws, client_ws):
        if ws is None:
            continue

        host_header = ws.headers.get("x-forwarded-host") or ws.headers.get("host")
        parsed = _extract_host_name(host_header)
        if parsed:
            return parsed

    return candidate or DEFAULT_RELAY_HOST


def _extract_host_name(host_header: str | None) -> str:
    if not host_header:
        return ""

    # Header may be a CSV list; use the first host.
    first = host_header.split(",", 1)[0].strip()
    if not first:
        return ""

    # IPv6 host with brackets, possibly including :port.
    if first.startswith("["):
        end = first.find("]")
        if end > 1:
            return first[1:end]

    # Standard host:port form.
    if ":" in first:
        host, _ = first.rsplit(":", 1)
        return host.strip()

    return first


def _resolve_host_endpoint_port(metadata: dict[str, Any], fallback_port: int) -> int:
    if not metadata:
        return fallback_port

    value = metadata.get("listen_port")
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return fallback_port

    if 1 <= parsed <= 65535:
        return parsed

    return fallback_port


async def _punch_timeout_watchdog(state: RendezvousState, session_id: str, relay_host: str, relay_port: int) -> None:
    try:
        await asyncio.sleep(PUNCH_TIMEOUT_SECONDS)
        await _request_port_map_for_session(state, session_id, relay_host=relay_host, relay_port=relay_port)
    except asyncio.CancelledError:
        return


async def _port_map_timeout_watchdog(state: RendezvousState, session_id: str, relay_host: str, relay_port: int) -> None:
    try:
        await asyncio.sleep(PORT_MAP_TIMEOUT_SECONDS)
        session = await state.mark_relay_requested(session_id)
        if not session:
            return
        if session.state == "relay_requested":
            await _send_relay_to_both(state, session_id, relay_host, relay_port)
    except asyncio.CancelledError:
        return


async def _request_port_map_for_session(state: RendezvousState, session_id: str, relay_host: str, relay_port: int) -> None:
    session = await state.mark_map_requested(session_id)
    if not session:
        return

    if session.state != "map_requested":
        return

    host = await state.get_host(session.host_id)
    if not host:
        relay_state = await state.mark_relay_requested(session_id)
        if relay_state and relay_state.state == "relay_requested":
            await _send_relay_to_both(state, session_id, relay_host, relay_port)
        return

    listen_port = _resolve_host_endpoint_port(host.metadata, host.public_port)
    await _send_model(
        host.ws,
        RequestPortMapMessage(
            type="request_port_map",
            session_id=session_id,
            internal_port=listen_port,
        ),
    )

    timeout_task = asyncio.create_task(
        _port_map_timeout_watchdog(state, session_id, relay_host=relay_host, relay_port=relay_port)
    )
    await state.attach_timeout_task(session_id, timeout_task)


async def handle_host_ws(state: RendezvousState, websocket: WebSocket, relay_host: str, relay_port: int) -> None:
    await websocket.accept()
    host_id: str | None = None

    try:
        while True:
            payload = await websocket.receive_json()
            try:
                msg = validate_host_inbound(payload)
            except Exception as exc:
                if is_validation_error(exc):
                    await _send_error(websocket, "invalid_payload", "Invalid host message payload")
                else:
                    await _send_error(websocket, "unsupported_message_type", str(exc))
                continue

            if isinstance(msg, RegisterHostMessage):
                ip, port = _peer_endpoint(websocket)
                await state.register_host(
                    host_id=msg.host_id,
                    ws=websocket,
                    public_ip=ip,
                    public_port=port,
                    max_clients=msg.max_clients,
                    metadata=msg.metadata,
                )
                host_id = msg.host_id
                continue

            if isinstance(msg, HostPunchResultMessage):
                if not host_id:
                    await _send_error(websocket, "not_registered", "Host must register before sending punch_result")
                    continue
                if msg.host_id != host_id:
                    await _send_error(websocket, "host_mismatch", "Host ID does not match registered host", msg.session_id)
                    continue

                session = await state.get_session(msg.session_id)
                if not session:
                    await _send_error(websocket, "not_found", "Unknown session", msg.session_id)
                    continue
                if session.host_id != host_id or session.client_id != msg.client_id:
                    await _send_error(websocket, "session_mismatch", "Session ownership mismatch", msg.session_id)
                    continue

                session = await state.update_punch_result(
                    session_id=msg.session_id,
                    from_host=True,
                    success=msg.success,
                )

                if session.state == "relay_requested":
                    await _send_relay_to_both(state, msg.session_id, relay_host, relay_port)
                continue

            if isinstance(msg, HostPortMapResultMessage):
                if not host_id:
                    await _send_error(websocket, "not_registered", "Host must register before sending port_map_result")
                    continue
                if msg.host_id != host_id:
                    await _send_error(websocket, "host_mismatch", "Host ID does not match registered host", msg.session_id)
                    continue

                session = await state.get_session(msg.session_id)
                if not session:
                    await _send_error(websocket, "not_found", "Unknown session", msg.session_id)
                    continue
                if session.host_id != host_id:
                    await _send_error(websocket, "session_mismatch", "Session ownership mismatch", msg.session_id)
                    continue

                if msg.success and msg.public_port:
                    updated = await state.set_mapped_endpoint(msg.session_id, msg.public_ip, msg.public_port)
                    if not updated:
                        continue

                    client = await state.get_client(updated.client_id)
                    host = await state.get_host(updated.host_id)
                    if not client or not host:
                        continue

                    mapped_ip = (msg.public_ip or "").strip() or host.public_ip
                    await _send_model(
                        client.ws,
                        HostEndpointMessage(
                            type="host_endpoint",
                            host_id=updated.host_id,
                            host_public_ip=mapped_ip,
                            host_public_port=msg.public_port,
                            session_id=updated.session_id,
                        ),
                    )
                    continue

                relay_state = await state.mark_relay_requested(msg.session_id)
                if relay_state and relay_state.state == "relay_requested":
                    await _send_relay_to_both(state, msg.session_id, relay_host, relay_port)

    except WebSocketDisconnect:
        pass
    finally:
        if host_id:
            await state.close_sessions_for_host(host_id)
            await state.unregister_host(host_id)
        await state.unregister_host_by_ws(websocket)


async def handle_client_ws(
    state: RendezvousState,
    websocket: WebSocket,
    relay_host: str,
    relay_port: int,
    force_relay: bool = False,
) -> None:
    await websocket.accept()
    client_id: str | None = None

    try:
        while True:
            payload = await websocket.receive_json()
            try:
                msg = validate_client_inbound(payload)
            except Exception as exc:
                if is_validation_error(exc):
                    await _send_error(websocket, "invalid_payload", "Invalid client message payload")
                else:
                    await _send_error(websocket, "unsupported_message_type", str(exc))
                continue

            if isinstance(msg, RegisterClientMessage):
                ip, port = _peer_endpoint(websocket)
                await state.register_client(
                    client_id=msg.client_id,
                    ws=websocket,
                    public_ip=ip,
                    public_port=port,
                )
                client_id = msg.client_id
                continue

            if not client_id:
                await _send_error(websocket, "not_registered", "Client must register before this operation")
                continue

            if isinstance(msg, ListHostsMessage):
                hosts = await state.list_hosts_for_client()
                host_summaries = [
                    HostSummary(
                        host_id=h.host_id,
                        metadata=h.metadata,
                        current_clients=h.current_clients,
                        max_clients=h.max_clients,
                    )
                    for h in hosts
                ]
                await _send_model(websocket, HostListMessage(type="host_list", hosts=host_summaries))
                continue

            if isinstance(msg, ConnectRequestMessage):
                if msg.client_id != client_id:
                    await _send_error(websocket, "client_mismatch", "Client ID does not match registered client")
                    continue

                host = await state.get_host(msg.host_id)
                client = await state.get_client(msg.client_id)

                if not host:
                    await _send_error(websocket, "not_found", f"Host {msg.host_id} not found")
                    continue
                if not client:
                    await _send_error(websocket, "not_found", f"Client {msg.client_id} not registered")
                    continue
                if host.current_clients >= host.max_clients:
                    await _send_error(websocket, "host_full", f"Host {msg.host_id} is at max capacity")
                    continue

                session = await state.create_session(host_id=msg.host_id, client_id=msg.client_id)

                await _send_model(
                    host.ws,
                    IncomingClientMessage(
                        type="incoming_client",
                        client_id=msg.client_id,
                        client_public_ip=client.public_ip,
                        client_public_port=client.public_port,
                        session_id=session.session_id,
                    ),
                )

                await _send_model(
                    websocket,
                    HostEndpointMessage(
                        type="host_endpoint",
                        host_id=msg.host_id,
                        host_public_ip=host.public_ip,
                        host_public_port=_resolve_host_endpoint_port(host.metadata, host.public_port),
                        session_id=session.session_id,
                    ),
                )

                start_msg = StartPunchMessage(type="start_punch", session_id=session.session_id)
                await _send_model(host.ws, start_msg)
                await _send_model(websocket, start_msg)

                if force_relay:
                    relay_state = await state.mark_relay_requested(session.session_id)
                    if relay_state and relay_state.state == "relay_requested":
                        await _send_relay_to_both(state, session.session_id, relay_host, relay_port)
                    continue

                timeout_task = asyncio.create_task(
                    _punch_timeout_watchdog(state, session.session_id, relay_host=relay_host, relay_port=relay_port)
                )
                await state.attach_timeout_task(session.session_id, timeout_task)
                continue

            if isinstance(msg, ClientRequestPortMapMessage):
                if msg.client_id != client_id:
                    await _send_error(websocket, "client_mismatch", "Client ID does not match registered client", msg.session_id)
                    continue

                session = await state.get_session(msg.session_id)
                if not session:
                    await _send_error(websocket, "not_found", "Unknown session", msg.session_id)
                    continue
                if session.client_id != client_id or session.host_id != msg.host_id:
                    await _send_error(websocket, "session_mismatch", "Session ownership mismatch", msg.session_id)
                    continue

                await _request_port_map_for_session(state, msg.session_id, relay_host=relay_host, relay_port=relay_port)
                continue

            # Client punch result
            if msg.client_id != client_id:
                await _send_error(websocket, "client_mismatch", "Client ID does not match registered client", msg.session_id)
                continue

            session = await state.get_session(msg.session_id)
            if not session:
                await _send_error(websocket, "not_found", "Unknown session", msg.session_id)
                continue
            if session.client_id != client_id or session.host_id != msg.host_id:
                await _send_error(websocket, "session_mismatch", "Session ownership mismatch", msg.session_id)
                continue

            session = await state.update_punch_result(
                session_id=msg.session_id,
                from_host=False,
                success=msg.success,
            )

            if session.state == "relay_requested":
                await _send_relay_to_both(state, msg.session_id, relay_host, relay_port)

    except WebSocketDisconnect:
        pass
    finally:
        if client_id:
            await state.close_sessions_for_client(client_id)
            await state.unregister_client(client_id)
        await state.unregister_client_by_ws(websocket)
