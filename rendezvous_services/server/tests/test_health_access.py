from __future__ import annotations

import unittest

from server.main import is_health_request_allowed


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


if __name__ == "__main__":
    unittest.main()
