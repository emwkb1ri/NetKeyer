import os
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest.mock import patch

from service_version import (
    DEFAULT_PROTOCOL_VERSION,
    load_build_metadata,
    load_protocol_version,
    load_services_version,
    load_version_block,
)


class TestServiceVersion(unittest.TestCase):
    def test_load_services_version_from_pyproject(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            pyproject = Path(tmp) / "pyproject.toml"
            pyproject.write_text(
                textwrap.dedent(
                    """
                    [project]
                    name = "netkeyer-rendezvous-services"
                    version = "9.8.7"
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )

            with patch.dict(os.environ, {}, clear=False):
                version = load_services_version(pyproject)

            self.assertEqual(version, "9.8.7")

    def test_load_services_version_prefers_env_override(self) -> None:
        with patch.dict(os.environ, {"RENDEZVOUS_SERVICES_VERSION": "2.3.4"}, clear=False):
            version = load_services_version()

        self.assertEqual(version, "2.3.4")

    def test_load_protocol_version_defaults_for_invalid_values(self) -> None:
        with patch.dict(os.environ, {"RENDEZVOUS_SERVICES_PROTOCOL_VERSION": "abc"}, clear=False):
            self.assertEqual(load_protocol_version(), DEFAULT_PROTOCOL_VERSION)

        with patch.dict(os.environ, {"RENDEZVOUS_SERVICES_PROTOCOL_VERSION": "0"}, clear=False):
            self.assertEqual(load_protocol_version(), DEFAULT_PROTOCOL_VERSION)

    def test_load_protocol_version_from_env(self) -> None:
        with patch.dict(os.environ, {"RENDEZVOUS_SERVICES_PROTOCOL_VERSION": "3"}, clear=False):
            self.assertEqual(load_protocol_version(), 3)

    def test_load_build_metadata(self) -> None:
        with patch.dict(
            os.environ,
            {
                "RENDEZVOUS_SERVICES_BUILD_TAG": "rs-v0.2.0",
                "RENDEZVOUS_SERVICES_BUILD_COMMIT": "abcdef1",
                "RENDEZVOUS_SERVICES_BUILD_DATE": "2026-08-10T00:00:00Z",
            },
            clear=False,
        ):
            metadata = load_build_metadata()

        self.assertEqual(metadata["tag"], "rs-v0.2.0")
        self.assertEqual(metadata["commit"], "abcdef1")
        self.assertEqual(metadata["built_at_utc"], "2026-08-10T00:00:00Z")

    def test_load_version_block_includes_component(self) -> None:
        with patch.dict(
            os.environ,
            {
                "RENDEZVOUS_SERVICES_VERSION": "5.6.7",
                "RENDEZVOUS_SERVICES_PROTOCOL_VERSION": "2",
                "RENDEZVOUS_SERVICES_BUILD_TAG": "rs-v5.6.7",
            },
            clear=False,
        ):
            block = load_version_block(component="rendezvous")

        self.assertEqual(block["services_version"], "5.6.7")
        self.assertEqual(block["protocol_version"], 2)
        self.assertEqual(block["component"], "rendezvous")
        self.assertEqual(block["build"]["tag"], "rs-v5.6.7")


if __name__ == "__main__":
    unittest.main()
