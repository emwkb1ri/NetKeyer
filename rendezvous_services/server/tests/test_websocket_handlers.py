import asyncio
import types
import unittest

from fastapi import WebSocketDisconnect

from server import websocket_handlers as handlers
from server.state import RendezvousState


_DISCONNECT = object()


class FakeWebSocket:
    def __init__(self, host: str, port: int) -> None:
        self.client = types.SimpleNamespace(host=host, port=port)
        self._incoming: asyncio.Queue = asyncio.Queue()
        self.sent: list[dict] = []
        self.accepted = False

    async def accept(self) -> None:
        self.accepted = True

    async def receive_json(self) -> dict:
        item = await self._incoming.get()
        if item is _DISCONNECT:
            raise WebSocketDisconnect(code=1000)
        return item

    async def send_json(self, payload: dict) -> None:
        self.sent.append(payload)

    async def push(self, payload: dict) -> None:
        await self._incoming.put(payload)

    async def disconnect(self) -> None:
        await self._incoming.put(_DISCONNECT)


class TestWebSocketHandlers(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.state = RendezvousState()
        self.host_ws = FakeWebSocket("203.0.113.10", 51000)
        self.client_ws = FakeWebSocket("198.51.100.22", 52000)

    async def _start_handlers(self):
        host_task = asyncio.create_task(
            handlers.handle_host_ws(self.state, self.host_ws, relay_host="relay.test", relay_port=49921)
        )
        client_task = asyncio.create_task(
            handlers.handle_client_ws(self.state, self.client_ws, relay_host="relay.test", relay_port=49921)
        )
        return host_task, client_task

    async def _stop_handlers(self, host_task: asyncio.Task, client_task: asyncio.Task) -> None:
        await self.host_ws.disconnect()
        await self.client_ws.disconnect()
        await asyncio.wait_for(asyncio.gather(host_task, client_task), timeout=2)

    def _sent_types(self, ws: FakeWebSocket) -> list[str]:
        return [m.get("type", "") for m in ws.sent]

    async def test_connect_request_emits_host_endpoint_and_start_punch(self) -> None:
        host_task, client_task = await self._start_handlers()

        await self.host_ws.push(
            {
                "type": "register_host",
                "host_id": "host-1",
                "max_clients": 5,
                "metadata": {"name": "Host1"},
            }
        )
        await self.client_ws.push(
            {
                "type": "register_client",
                "client_id": "client-1",
            }
        )
        await self.client_ws.push(
            {
                "type": "connect_request",
                "client_id": "client-1",
                "host_id": "host-1",
            }
        )

        await asyncio.sleep(0.1)

        host_types = self._sent_types(self.host_ws)
        client_types = self._sent_types(self.client_ws)

        self.assertIn("incoming_client", host_types)
        self.assertIn("start_punch", host_types)
        self.assertIn("host_endpoint", client_types)
        self.assertIn("start_punch", client_types)

        await self._stop_handlers(host_task, client_task)

    async def test_connect_request_client_id_mismatch_rejected(self) -> None:
        host_task, client_task = await self._start_handlers()

        await self.host_ws.push(
            {
                "type": "register_host",
                "host_id": "host-1",
                "max_clients": 5,
                "metadata": {},
            }
        )
        await self.client_ws.push(
            {
                "type": "register_client",
                "client_id": "client-a",
            }
        )
        await self.client_ws.push(
            {
                "type": "connect_request",
                "client_id": "client-b",
                "host_id": "host-1",
            }
        )

        await asyncio.sleep(0.05)

        self.assertTrue(self.client_ws.sent)
        self.assertEqual(self.client_ws.sent[-1].get("type"), "error")
        self.assertEqual(self.client_ws.sent[-1].get("code"), "client_mismatch")

        await self._stop_handlers(host_task, client_task)

    async def test_connect_request_rejected_when_host_full(self) -> None:
        host_task, client_task = await self._start_handlers()

        await self.host_ws.push(
            {
                "type": "register_host",
                "host_id": "host-1",
                "max_clients": 1,
                "metadata": {},
            }
        )
        await self.client_ws.push(
            {
                "type": "register_client",
                "client_id": "client-1",
            }
        )

        await asyncio.sleep(0.05)
        host = await self.state.get_host("host-1")
        self.assertIsNotNone(host)
        assert host is not None
        host.current_clients = host.max_clients

        await self.client_ws.push(
            {
                "type": "connect_request",
                "client_id": "client-1",
                "host_id": "host-1",
            }
        )

        await asyncio.sleep(0.05)

        self.assertTrue(self.client_ws.sent)
        self.assertEqual(self.client_ws.sent[-1].get("type"), "error")
        self.assertEqual(self.client_ws.sent[-1].get("code"), "host_full")

        await self._stop_handlers(host_task, client_task)

    async def test_host_punch_result_requires_registration(self) -> None:
        host_task = asyncio.create_task(
            handlers.handle_host_ws(self.state, self.host_ws, relay_host="relay.test", relay_port=49921)
        )

        await self.host_ws.push(
            {
                "type": "punch_result",
                "success": False,
                "client_id": "client-1",
                "host_id": "host-1",
                "session_id": "sess-1",
            }
        )

        await asyncio.sleep(0.05)

        self.assertTrue(self.host_ws.sent)
        self.assertEqual(self.host_ws.sent[-1].get("type"), "error")
        self.assertEqual(self.host_ws.sent[-1].get("code"), "not_registered")

        await self.host_ws.disconnect()
        await asyncio.wait_for(host_task, timeout=2)

    async def test_client_punch_unknown_session_rejected(self) -> None:
        host_task, client_task = await self._start_handlers()

        await self.client_ws.push(
            {
                "type": "register_client",
                "client_id": "client-1",
            }
        )
        await self.client_ws.push(
            {
                "type": "punch_result",
                "success": False,
                "client_id": "client-1",
                "host_id": "host-1",
                "session_id": "missing-session",
            }
        )

        await asyncio.sleep(0.05)

        self.assertTrue(self.client_ws.sent)
        self.assertEqual(self.client_ws.sent[-1].get("type"), "error")
        self.assertEqual(self.client_ws.sent[-1].get("code"), "not_found")

        await self._stop_handlers(host_task, client_task)

    async def test_client_disconnect_cleans_session_and_host_capacity(self) -> None:
        host_task, client_task = await self._start_handlers()

        await self.host_ws.push(
            {
                "type": "register_host",
                "host_id": "host-1",
                "max_clients": 5,
                "metadata": {},
            }
        )
        await self.client_ws.push(
            {
                "type": "register_client",
                "client_id": "client-1",
            }
        )
        await self.client_ws.push(
            {
                "type": "connect_request",
                "client_id": "client-1",
                "host_id": "host-1",
            }
        )

        await asyncio.sleep(0.1)

        host = await self.state.get_host("host-1")
        self.assertIsNotNone(host)
        assert host is not None
        self.assertEqual(host.current_clients, 1)

        endpoint_msgs = [m for m in self.client_ws.sent if m.get("type") == "host_endpoint"]
        self.assertTrue(endpoint_msgs)
        session_id = endpoint_msgs[-1]["session_id"]
        self.assertIsNotNone(await self.state.get_session(session_id))

        await self.client_ws.disconnect()
        await asyncio.wait_for(client_task, timeout=2)

        host_after = await self.state.get_host("host-1")
        self.assertIsNotNone(host_after)
        assert host_after is not None
        self.assertEqual(host_after.current_clients, 0)
        self.assertIsNone(await self.state.get_session(session_id))

        await self.host_ws.disconnect()
        await asyncio.wait_for(host_task, timeout=2)

    async def test_timeout_triggers_use_relay(self) -> None:
        previous_timeout = handlers.PUNCH_TIMEOUT_SECONDS
        handlers.PUNCH_TIMEOUT_SECONDS = 0.05

        host_task, client_task = await self._start_handlers()
        try:
            await self.host_ws.push(
                {
                    "type": "register_host",
                    "host_id": "host-1",
                    "max_clients": 5,
                    "metadata": {},
                }
            )
            await self.client_ws.push(
                {
                    "type": "register_client",
                    "client_id": "client-1",
                }
            )
            await self.client_ws.push(
                {
                    "type": "connect_request",
                    "client_id": "client-1",
                    "host_id": "host-1",
                }
            )

            await asyncio.sleep(0.15)

            self.assertIn("use_relay", self._sent_types(self.host_ws))
            self.assertIn("use_relay", self._sent_types(self.client_ws))
        finally:
            handlers.PUNCH_TIMEOUT_SECONDS = previous_timeout
            await self._stop_handlers(host_task, client_task)


if __name__ == "__main__":
    unittest.main()
