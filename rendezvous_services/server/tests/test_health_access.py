from __future__ import annotations

import types
import unittest

from server.main import health, is_health_request_allowed


class HealthAccessPolicyTests(unittest.TestCase):
    def test_private_mode_allows_loopback(self) -> None:
        self.assertTrue(is_health_request_allowed("127.0.0.1", mode="private"))
        self.assertTrue(is_health_request_allowed("::1", mode="private"))

    def test_private_mode_denies_public_ip(self) -> None:
        self.assertFalse(is_health_request_allowed("8.8.8.8", mode="private"))

    def test_cidr_mode_allows_in_cidr(self) -> None:
        allowed = ["203.0.113.0/24"]
        self.assertTrue(is_health_request_allowed("203.0.113.8", mode="cidr", allowed_cidrs=allowed))

    def test_cidr_mode_denies_outside_cidr(self) -> None:
        allowed = ["203.0.113.0/24"]
        self.assertFalse(is_health_request_allowed("198.51.100.10", mode="cidr", allowed_cidrs=allowed))

    def test_public_mode_allows_without_client_host(self) -> None:
        self.assertTrue(is_health_request_allowed(None, mode="public"))

    def test_disabled_mode_denies_all(self) -> None:
        self.assertFalse(is_health_request_allowed("127.0.0.1", mode="disabled"))


class HealthPayloadTests(unittest.IsolatedAsyncioTestCase):
    async def test_health_payload_includes_security_metrics(self) -> None:
        request = types.SimpleNamespace(client=types.SimpleNamespace(host="127.0.0.1"))

        payload = await health(request)

        self.assertIn("statistics", payload)
        stats = payload["statistics"]
        self.assertIn("security_metrics", stats)
        self.assertEqual(
            stats["security_metrics"],
            {
                "auth_failures": 0,
                "handshake_failures": 0,
                "replay_rejects": 0,
                "decrypt_failures": 0,
            },
        )


if __name__ == "__main__":
    unittest.main()
