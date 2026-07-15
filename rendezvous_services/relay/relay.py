from __future__ import annotations

import asyncio
import contextlib
import os
import time
from dataclasses import dataclass


VALID_ROLES = {"HOST", "CLIENT"}
HANDSHAKE_MAX_BYTES = 512


@dataclass
class PendingSession:
    session_id: str
    created_monotonic: float
    host_reader: asyncio.StreamReader | None = None
    host_writer: asyncio.StreamWriter | None = None
    client_reader: asyncio.StreamReader | None = None
    client_writer: asyncio.StreamWriter | None = None
    timeout_task: asyncio.Task | None = None
    relay_task: asyncio.Task | None = None


class RelayServer:
    def __init__(
        self,
        host: str = "0.0.0.0",
        port: int = 49921,
        session_timeout_seconds: float = 10.0,
        handshake_timeout_seconds: float = 5.0,
    ) -> None:
        self.host = host
        self.port = port
        self.session_timeout_seconds = session_timeout_seconds
        self.handshake_timeout_seconds = handshake_timeout_seconds
        self._server: asyncio.AbstractServer | None = None
        self._lock = asyncio.Lock()
        self._sessions: dict[str, PendingSession] = {}

    @property
    def sessions(self) -> dict[str, PendingSession]:
        return self._sessions

    @property
    def bound_port(self) -> int:
        if not self._server or not self._server.sockets:
            return self.port
        return int(self._server.sockets[0].getsockname()[1])

    async def start(self) -> None:
        self._server = await asyncio.start_server(self._handle_connection, self.host, self.port)

    async def stop(self) -> None:
        if self._server:
            self._server.close()
            await self._server.wait_closed()
            self._server = None

        async with self._lock:
            sessions = list(self._sessions.values())
            self._sessions.clear()

        for session in sessions:
            if session.timeout_task:
                session.timeout_task.cancel()
            if session.relay_task:
                session.relay_task.cancel()
            await self._close_writer(session.host_writer)
            await self._close_writer(session.client_writer)

    async def serve_forever(self) -> None:
        await self.start()
        assert self._server is not None
        async with self._server:
            await self._server.serve_forever()

    async def _handle_connection(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        session_id: str | None = None
        role: str | None = None
        attached = False

        try:
            session_id, role = await self._read_handshake(reader)

            async with self._lock:
                session = self._sessions.get(session_id)
                if session is None:
                    session = PendingSession(session_id=session_id, created_monotonic=time.monotonic())
                    self._sessions[session_id] = session
                    session.timeout_task = asyncio.create_task(self._session_timeout_watchdog(session_id))

                if role == "HOST":
                    if session.host_writer is not None:
                        await self._send_error_and_close(writer, "duplicate HOST for session")
                        return
                    session.host_reader = reader
                    session.host_writer = writer
                    attached = True
                else:
                    if session.client_writer is not None:
                        await self._send_error_and_close(writer, "duplicate CLIENT for session")
                        return
                    session.client_reader = reader
                    session.client_writer = writer
                    attached = True

                if session.host_writer and session.client_writer and session.relay_task is None:
                    if session.timeout_task:
                        session.timeout_task.cancel()
                        session.timeout_task = None
                    session.relay_task = asyncio.create_task(self._run_relay(session_id))

            # Keep the coroutine attached to this connection alive until the peer closes.
            await writer.wait_closed()
        except asyncio.IncompleteReadError:
            await self._close_writer(writer)
        except asyncio.TimeoutError:
            await self._send_error_and_close(writer, "handshake timeout")
        except ValueError as exc:
            await self._send_error_and_close(writer, str(exc))
        finally:
            if attached and session_id and role:
                await self._handle_single_side_disconnect(session_id, role)

    async def _read_handshake(self, reader: asyncio.StreamReader) -> tuple[str, str]:
        line = await asyncio.wait_for(reader.readline(), timeout=self.handshake_timeout_seconds)
        if not line:
            raise ValueError("empty handshake")
        if len(line) > HANDSHAKE_MAX_BYTES:
            raise ValueError("handshake too long")

        parts = line.decode("utf-8", errors="strict").strip().split()
        if len(parts) != 3 or parts[0] != "SESSION":
            raise ValueError("invalid handshake format")

        session_id = parts[1]
        role = parts[2].upper()
        if not session_id:
            raise ValueError("missing session id")
        if role not in VALID_ROLES:
            raise ValueError("invalid role")

        return session_id, role

    async def _session_timeout_watchdog(self, session_id: str) -> None:
        try:
            await asyncio.sleep(self.session_timeout_seconds)
            async with self._lock:
                session = self._sessions.pop(session_id, None)
            if session:
                await self._close_writer(session.host_writer)
                await self._close_writer(session.client_writer)
        except asyncio.CancelledError:
            return

    async def _run_relay(self, session_id: str) -> None:
        async with self._lock:
            session = self._sessions.get(session_id)
            if not session:
                return
            host_reader = session.host_reader
            host_writer = session.host_writer
            client_reader = session.client_reader
            client_writer = session.client_writer

        if not host_reader or not host_writer or not client_reader or not client_writer:
            return

        forward_host_to_client = asyncio.create_task(self._pipe(host_reader, client_writer))
        forward_client_to_host = asyncio.create_task(self._pipe(client_reader, host_writer))

        done, pending = await asyncio.wait(
            {forward_host_to_client, forward_client_to_host},
            return_when=asyncio.FIRST_COMPLETED,
        )

        for task in pending:
            task.cancel()
        for task in done:
            with contextlib.suppress(Exception):
                task.result()

        await self._close_writer(host_writer)
        await self._close_writer(client_writer)

        async with self._lock:
            session = self._sessions.pop(session_id, None)
            if session and session.timeout_task:
                session.timeout_task.cancel()

    async def _pipe(self, source: asyncio.StreamReader, sink: asyncio.StreamWriter) -> None:
        while True:
            data = await source.read(65536)
            if not data:
                break
            sink.write(data)
            await sink.drain()

    async def _handle_single_side_disconnect(self, session_id: str, role: str) -> None:
        async with self._lock:
            session = self._sessions.get(session_id)
            if not session:
                return

            host_active = session.host_writer and not session.host_writer.is_closing()
            client_active = session.client_writer and not session.client_writer.is_closing()
            if host_active and client_active:
                return

            # If one side disconnects while still pending, close the other side and clear the session.
            self._sessions.pop(session_id, None)
            timeout_task = session.timeout_task
            relay_task = session.relay_task
            host_writer = session.host_writer
            client_writer = session.client_writer

        if timeout_task:
            timeout_task.cancel()
        if relay_task:
            relay_task.cancel()
        await self._close_writer(host_writer)
        await self._close_writer(client_writer)

    async def _send_error_and_close(self, writer: asyncio.StreamWriter, message: str) -> None:
        with contextlib.suppress(Exception):
            writer.write(f"ERROR {message}\n".encode("utf-8"))
            await writer.drain()
        await self._close_writer(writer)

    async def _close_writer(self, writer: asyncio.StreamWriter | None) -> None:
        if not writer:
            return
        if writer.is_closing():
            with contextlib.suppress(Exception):
                await writer.wait_closed()
            return
        writer.close()
        with contextlib.suppress(Exception):
            await writer.wait_closed()


async def _main() -> None:
    relay_host = os.getenv("RELAY_HOST", "0.0.0.0")
    relay_port = int(os.getenv("RELAY_PORT", "49921"))
    session_timeout_seconds = float(os.getenv("RELAY_SESSION_TIMEOUT_SECONDS", "30"))
    handshake_timeout_seconds = float(os.getenv("RELAY_HANDSHAKE_TIMEOUT_SECONDS", "5"))

    server = RelayServer(
        host=relay_host,
        port=relay_port,
        session_timeout_seconds=session_timeout_seconds,
        handshake_timeout_seconds=handshake_timeout_seconds,
    )
    await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(_main())
