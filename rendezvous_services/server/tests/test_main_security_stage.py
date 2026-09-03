from __future__ import annotations

import importlib
import os
import unittest

from server import main as rendezvous_main


class SecurityStageConfigTests(unittest.TestCase):
    _ENV_KEYS = [
        "RENDEZVOUS_SECURITY_STAGE",
        "RENDEZVOUS_REQUIRE_SIGNED_TOKENS",
        "RENDEZVOUS_AUTH_ALLOW_LEGACY_NO_TOKEN",
        "RENDEZVOUS_REQUIRE_CONNECTION_GRANT",
        "RENDEZVOUS_JWT_REQUIRE_JTI",
        "RENDEZVOUS_JWT_REQUIRE_PROTOCOL_VERSION",
    ]

    def setUp(self) -> None:
        self._original = {key: os.environ.get(key) for key in self._ENV_KEYS}

    def tearDown(self) -> None:
        for key, value in self._original.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value
        importlib.reload(rendezvous_main)

    def _reload_with_env(self, stage: str, overrides: dict[str, str] | None = None):
        for key in self._ENV_KEYS:
            os.environ.pop(key, None)

        os.environ["RENDEZVOUS_SECURITY_STAGE"] = stage
        for key, value in (overrides or {}).items():
            os.environ[key] = value

        return importlib.reload(rendezvous_main)

    def test_compat_stage_defaults(self) -> None:
        module = self._reload_with_env("compat")

        self.assertFalse(module.REQUIRE_SIGNED_TOKENS)
        self.assertTrue(module.ALLOW_LEGACY_NO_TOKEN)
        self.assertFalse(module.REQUIRE_CONNECTION_GRANT)
        self.assertFalse(module.JWT_REQUIRE_JTI)
        self.assertFalse(module.JWT_REQUIRE_PROTOCOL_VERSION)

    def test_strict_stage_defaults(self) -> None:
        module = self._reload_with_env("strict")

        self.assertTrue(module.REQUIRE_SIGNED_TOKENS)
        self.assertFalse(module.ALLOW_LEGACY_NO_TOKEN)
        self.assertTrue(module.REQUIRE_CONNECTION_GRANT)
        self.assertTrue(module.JWT_REQUIRE_JTI)
        self.assertTrue(module.JWT_REQUIRE_PROTOCOL_VERSION)

    def test_explicit_env_overrides_stage_defaults(self) -> None:
        module = self._reload_with_env(
            "strict",
            overrides={
                "RENDEZVOUS_REQUIRE_SIGNED_TOKENS": "false",
                "RENDEZVOUS_AUTH_ALLOW_LEGACY_NO_TOKEN": "true",
                "RENDEZVOUS_REQUIRE_CONNECTION_GRANT": "false",
                "RENDEZVOUS_JWT_REQUIRE_JTI": "false",
                "RENDEZVOUS_JWT_REQUIRE_PROTOCOL_VERSION": "false",
            },
        )

        self.assertFalse(module.REQUIRE_SIGNED_TOKENS)
        self.assertTrue(module.ALLOW_LEGACY_NO_TOKEN)
        self.assertFalse(module.REQUIRE_CONNECTION_GRANT)
        self.assertFalse(module.JWT_REQUIRE_JTI)
        self.assertFalse(module.JWT_REQUIRE_PROTOCOL_VERSION)


if __name__ == "__main__":
    unittest.main()
