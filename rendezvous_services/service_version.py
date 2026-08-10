from __future__ import annotations

import os
from pathlib import Path
from typing import Any

try:
    import tomllib
except ModuleNotFoundError:  # pragma: no cover
    import tomli as tomllib  # type: ignore[no-redef]


DEFAULT_SERVICES_VERSION = "0.1.0"
DEFAULT_PROTOCOL_VERSION = 1


def _repo_root() -> Path:
    return Path(__file__).resolve().parent


def _pyproject_path() -> Path:
    return _repo_root() / "pyproject.toml"


def load_services_version(pyproject_path: Path | None = None) -> str:
    env_version = os.getenv("RENDEZVOUS_SERVICES_VERSION", "").strip()
    if env_version:
        return env_version

    path = pyproject_path or _pyproject_path()
    try:
        with path.open("rb") as f:
            data = tomllib.load(f)
        version = str(data.get("project", {}).get("version", "")).strip()
        if version:
            return version
    except Exception:
        pass

    return DEFAULT_SERVICES_VERSION


def load_protocol_version() -> int:
    value = os.getenv("RENDEZVOUS_SERVICES_PROTOCOL_VERSION", "").strip()
    if not value:
        return DEFAULT_PROTOCOL_VERSION

    try:
        parsed = int(value)
    except ValueError:
        return DEFAULT_PROTOCOL_VERSION

    if parsed < 1:
        return DEFAULT_PROTOCOL_VERSION

    return parsed


def load_build_metadata() -> dict[str, Any]:
    return {
        "tag": os.getenv("RENDEZVOUS_SERVICES_BUILD_TAG", "").strip(),
        "commit": os.getenv("RENDEZVOUS_SERVICES_BUILD_COMMIT", "").strip(),
        "built_at_utc": os.getenv("RENDEZVOUS_SERVICES_BUILD_DATE", "").strip(),
    }


def load_version_block(component: str) -> dict[str, Any]:
    services_version = load_services_version()
    protocol_version = load_protocol_version()
    build = load_build_metadata()

    return {
        "services_version": services_version,
        "protocol_version": protocol_version,
        "component": component,
        "build": build,
    }
