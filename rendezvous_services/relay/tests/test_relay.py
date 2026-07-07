import asyncio
import unittest

from relay.relay import RelayServer


class TestRelayServer(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.server = RelayServer(host="127.0.0.1", port=0, session_timeout_seconds=0.2, handshake_timeout_seconds=0.2)
        await self.server.start()
        self.port = self.server.bound_port

    async def asyncTearDown(self) -> None:
        await self.server.stop()

    async def _connect_with_handshake(self, session_id: str, role: str):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        writer.write(f"SESSION {session_id} {role}\n".encode("utf-8"))
        await writer.drain()
        return reader, writer

    async def test_invalid_handshake_is_rejected(self) -> None:
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        writer.write(b"BOGUS bad handshake\n")
        await writer.drain()

        line = await asyncio.wait_for(reader.readline(), timeout=1)
        self.assertTrue(line.startswith(b"ERROR"))

        writer.close()
        await writer.wait_closed()

    async def test_pairing_and_bidirectional_forwarding(self) -> None:
        host_reader, host_writer = await self._connect_with_handshake("session-a", "HOST")
        client_reader, client_writer = await self._connect_with_handshake("session-a", "CLIENT")

        host_payload = b"host-to-client-\x00-\xff"
        host_writer.write(host_payload)
        await host_writer.drain()
        self.assertEqual(await asyncio.wait_for(client_reader.readexactly(len(host_payload)), timeout=1), host_payload)

        client_payload = b"client-to-host-12345"
        client_writer.write(client_payload)
        await client_writer.drain()
        self.assertEqual(await asyncio.wait_for(host_reader.readexactly(len(client_payload)), timeout=1), client_payload)

        host_writer.close()
        await host_writer.wait_closed()
        client_writer.close()
        await client_writer.wait_closed()

    async def test_reversed_order_pairing(self) -> None:
        client_reader, client_writer = await self._connect_with_handshake("session-b", "CLIENT")
        host_reader, host_writer = await self._connect_with_handshake("session-b", "HOST")

        payload = b"reverse-order"
        client_writer.write(payload)
        await client_writer.drain()
        self.assertEqual(await asyncio.wait_for(host_reader.readexactly(len(payload)), timeout=1), payload)

        host_writer.close()
        await host_writer.wait_closed()
        client_writer.close()
        await client_writer.wait_closed()

    async def test_incomplete_session_times_out_and_closes_socket(self) -> None:
        host_reader, host_writer = await self._connect_with_handshake("session-timeout", "HOST")
        await asyncio.sleep(0.35)

        # Timeout should close pending single-sided sessions.
        self.assertEqual(await asyncio.wait_for(host_reader.read(), timeout=1), b"")
        self.assertNotIn("session-timeout", self.server.sessions)

        host_writer.close()
        await host_writer.wait_closed()

    async def test_disconnect_propagates_and_cleans_session(self) -> None:
        host_reader, host_writer = await self._connect_with_handshake("session-close", "HOST")
        client_reader, client_writer = await self._connect_with_handshake("session-close", "CLIENT")

        host_writer.write(b"ping")
        await host_writer.drain()
        self.assertEqual(await asyncio.wait_for(client_reader.readexactly(4), timeout=1), b"ping")

        host_writer.close()
        await host_writer.wait_closed()

        self.assertEqual(await asyncio.wait_for(client_reader.read(), timeout=1), b"")
        self.assertNotIn("session-close", self.server.sessions)

        client_writer.close()
        await client_writer.wait_closed()

    async def test_duplicate_role_rejected_with_error_text(self) -> None:
        first_host_reader, first_host_writer = await self._connect_with_handshake("session-dup", "HOST")
        second_host_reader, second_host_writer = await self._connect_with_handshake("session-dup", "HOST")

        error_line = await asyncio.wait_for(second_host_reader.readline(), timeout=1)
        self.assertEqual(error_line.decode("utf-8").strip(), "ERROR duplicate HOST for session")

        second_host_writer.close()
        await second_host_writer.wait_closed()

        # Keep first connection valid by pairing with the opposite role.
        client_reader, client_writer = await self._connect_with_handshake("session-dup", "CLIENT")
        first_host_writer.write(b"ok")
        await first_host_writer.drain()
        self.assertEqual(await asyncio.wait_for(client_reader.readexactly(2), timeout=1), b"ok")

        first_host_writer.close()
        await first_host_writer.wait_closed()
        client_writer.close()
        await client_writer.wait_closed()

    async def test_sustained_relay_throughput_stability(self) -> None:
        host_reader, host_writer = await self._connect_with_handshake("session-load", "HOST")
        client_reader, client_writer = await self._connect_with_handshake("session-load", "CLIENT")

        host_to_client = b"A" * 8192
        client_to_host = b"B" * 8192
        iterations = 20

        for _ in range(iterations):
            host_writer.write(host_to_client)
            client_writer.write(client_to_host)
            await host_writer.drain()
            await client_writer.drain()

            recv_at_client = await asyncio.wait_for(client_reader.readexactly(len(host_to_client)), timeout=1)
            recv_at_host = await asyncio.wait_for(host_reader.readexactly(len(client_to_host)), timeout=1)
            self.assertEqual(recv_at_client, host_to_client)
            self.assertEqual(recv_at_host, client_to_host)

        host_writer.close()
        await host_writer.wait_closed()
        client_writer.close()
        await client_writer.wait_closed()


if __name__ == "__main__":
    unittest.main()
