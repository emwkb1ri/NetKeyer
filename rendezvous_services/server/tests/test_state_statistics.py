import unittest

from server.state import RendezvousState


class FakeWebSocket:
    def __init__(self, host: str, port: int) -> None:
        self.client = type("Client", (), {"host": host, "port": port})()


class TestStateStatistics(unittest.IsolatedAsyncioTestCase):
    async def test_empty_statistics(self) -> None:
        state = RendezvousState()

        stats = await state.get_statistics_snapshot()

        self.assertEqual(stats["counts"], {"hosts": 0, "clients": 0, "sessions": 0})
        self.assertEqual(stats["session_type_counts"], {"direct": 0, "mapped": 0, "relay": 0})
        self.assertEqual(
            stats["security_metrics"],
            {
                "auth_failures": 0,
                "handshake_failures": 0,
                "replay_rejects": 0,
                "decrypt_failures": 0,
            },
        )
        self.assertEqual(stats["hosts"], [])
        self.assertEqual(stats["clients"], [])
        self.assertEqual(stats["sessions"], [])

    async def test_security_metric_recording_and_classification(self) -> None:
        state = RendezvousState()

        await state.record_security_failure(auth_failure=True, detail="missing access token")
        await state.record_security_failure(handshake_failure=True, detail="missing connection grant token")
        await state.record_security_failure(handshake_failure=True, detail="replayed connection grant rejected")
        await state.record_security_failure(handshake_failure=True, detail="invalid connection grant token")

        stats = await state.get_statistics_snapshot()
        self.assertEqual(stats["security_metrics"]["auth_failures"], 1)
        self.assertEqual(stats["security_metrics"]["handshake_failures"], 3)
        self.assertEqual(stats["security_metrics"]["replay_rejects"], 1)
        self.assertEqual(stats["security_metrics"]["decrypt_failures"], 1)

    async def test_statistics_with_direct_mapped_and_relay_sessions(self) -> None:
        state = RendezvousState()

        host1_ws = FakeWebSocket("198.51.100.10", 51000)
        host2_ws = FakeWebSocket("198.51.100.11", 51001)
        client1_ws = FakeWebSocket("203.0.113.20", 52000)
        client2_ws = FakeWebSocket("203.0.113.21", 52001)
        client3_ws = FakeWebSocket("203.0.113.22", 52002)

        await state.register_host("host-a", host1_ws, "198.51.100.10", 49923, 5, {"name": "Host A"})
        await state.register_host("host-b", host2_ws, "198.51.100.11", 49923, 5, {"name": "Host B"})
        await state.register_client("client-a", client1_ws, "203.0.113.20", 53000)
        await state.register_client("client-b", client2_ws, "203.0.113.21", 53001)
        await state.register_client("client-c", client3_ws, "203.0.113.22", 53002)

        direct = await state.create_session("host-a", "client-a")
        mapped = await state.create_session("host-a", "client-b")
        relay = await state.create_session("host-b", "client-c")

        await state.update_punch_result(direct.session_id, from_host=True, success=True)
        await state.mark_map_requested(mapped.session_id)
        await state.set_mapped_endpoint(mapped.session_id, "198.51.100.10", 49923)
        await state.mark_relay_requested(relay.session_id)
        await state.update_punch_result(mapped.session_id, from_host=False, success=True)
        await state.update_punch_result(relay.session_id, from_host=False, success=True)

        stats = await state.get_statistics_snapshot()

        self.assertEqual(stats["counts"], {"hosts": 2, "clients": 3, "sessions": 3})
        self.assertEqual(stats["session_type_counts"], {"direct": 1, "mapped": 1, "relay": 1})

        host_a = next(h for h in stats["hosts"] if h["host_id"] == "host-a")
        host_b = next(h for h in stats["hosts"] if h["host_id"] == "host-b")
        self.assertEqual(host_a["current_clients"], 2)
        self.assertEqual(host_b["current_clients"], 1)
        self.assertEqual(len(host_a["active_sessions"]), 2)
        self.assertEqual(len(host_b["active_sessions"]), 1)

        client_a = next(c for c in stats["clients"] if c["client_id"] == "client-a")
        client_b = next(c for c in stats["clients"] if c["client_id"] == "client-b")
        client_c = next(c for c in stats["clients"] if c["client_id"] == "client-c")
        self.assertEqual(client_a["connected_host"], "host-a")
        self.assertEqual(client_b["connected_host"], "host-a")
        self.assertEqual(client_c["connected_host"], "host-b")

        sessions_by_id = {s["session_id"]: s for s in stats["sessions"]}
        self.assertEqual(sessions_by_id[direct.session_id]["type"], "direct")
        self.assertEqual(sessions_by_id[mapped.session_id]["type"], "mapped")
        self.assertEqual(sessions_by_id[relay.session_id]["type"], "relay")
        self.assertEqual(sessions_by_id[mapped.session_id]["state"], "mapped_connected")
        self.assertEqual(sessions_by_id[relay.session_id]["state"], "relay_connected")


if __name__ == "__main__":
    unittest.main()
