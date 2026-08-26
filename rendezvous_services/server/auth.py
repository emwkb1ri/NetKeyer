from __future__ import annotations

from dataclasses import dataclass
import threading
import time
from typing import Any

import jwt
from fastapi import WebSocket


class AuthError(Exception):
    pass


@dataclass(frozen=True)
class AuthConfig:
    require_signed_tokens: bool
    allow_legacy_no_token: bool
    jwt_secret: str
    jwt_issuer: str
    jwt_audience: str
    required_scope_host: str
    required_scope_client: str
    jti_replay_ttl_seconds: int
    jti_replay_cache_max_entries: int
    require_jti: bool


class _ReplayCache:
    def __init__(self) -> None:
        self._items: dict[str, float] = {}
        self._lock = threading.Lock()

    def _evict_expired(self, now: float) -> None:
        expired = [key for key, expires_at in self._items.items() if expires_at <= now]
        for key in expired:
            self._items.pop(key, None)

    def check_and_store(self, key: str, ttl_seconds: int, max_entries: int) -> bool:
        if ttl_seconds <= 0:
            return True

        now = time.time()
        with self._lock:
            self._evict_expired(now)

            if key in self._items:
                return False

            # Defensive bound to avoid unbounded growth under attack.
            if max_entries > 0 and len(self._items) >= max_entries:
                oldest_key = min(self._items, key=self._items.get)
                self._items.pop(oldest_key, None)

            self._items[key] = now + ttl_seconds
            return True


REPLAY_CACHE = _ReplayCache()


def parse_bearer_token(auth_header: str | None) -> str:
    if not auth_header:
        return ""

    value = auth_header.strip()
    if not value:
        return ""

    prefix = "bearer "
    if value.lower().startswith(prefix):
        return value[len(prefix) :].strip()

    return ""


def extract_websocket_token(websocket: WebSocket) -> str:
    query_token = (websocket.query_params.get("access_token") or "").strip()
    if query_token:
        return query_token

    auth_header = websocket.headers.get("authorization")
    return parse_bearer_token(auth_header)


def _extract_roles(claims: dict[str, Any]) -> set[str]:
    roles: set[str] = set()

    role_value = claims.get("role")
    if isinstance(role_value, str) and role_value.strip():
        roles.add(role_value.strip().lower())

    roles_value = claims.get("roles")
    if isinstance(roles_value, str) and roles_value.strip():
        roles.add(roles_value.strip().lower())
    elif isinstance(roles_value, list):
        for item in roles_value:
            if isinstance(item, str) and item.strip():
                roles.add(item.strip().lower())

    return roles


def _extract_scopes(claims: dict[str, Any]) -> set[str]:
    scopes: set[str] = set()

    scope_value = claims.get("scope")
    if isinstance(scope_value, str):
        for item in scope_value.split():
            value = item.strip().lower()
            if value:
                scopes.add(value)
    elif isinstance(scope_value, list):
        for item in scope_value:
            if isinstance(item, str):
                value = item.strip().lower()
                if value:
                    scopes.add(value)

    scopes_value = claims.get("scopes")
    if isinstance(scopes_value, str):
        for item in scopes_value.split():
            value = item.strip().lower()
            if value:
                scopes.add(value)
    elif isinstance(scopes_value, list):
        for item in scopes_value:
            if isinstance(item, str):
                value = item.strip().lower()
                if value:
                    scopes.add(value)

    return scopes


def _required_scope_for_role(config: AuthConfig, required_role: str | None) -> str:
    role = (required_role or "").strip().lower()
    if role == "host":
        return config.required_scope_host.strip().lower()
    if role == "client":
        return config.required_scope_client.strip().lower()
    return ""


def validate_access_token(token: str, config: AuthConfig, required_role: str | None = None) -> dict[str, Any]:
    if not token:
        raise AuthError("missing access token")

    if not config.jwt_secret.strip():
        raise AuthError("jwt secret is not configured")

    decode_kwargs: dict[str, Any] = {
        "algorithms": ["HS256"],
        "options": {"require": ["exp", "iat", "sub"]},
    }

    if config.jwt_issuer:
        decode_kwargs["issuer"] = config.jwt_issuer

    if config.jwt_audience:
        decode_kwargs["audience"] = config.jwt_audience

    try:
        claims = jwt.decode(token, config.jwt_secret, **decode_kwargs)
    except jwt.PyJWTError as ex:
        raise AuthError(f"invalid access token: {ex}") from ex

    if required_role:
        required = required_role.strip().lower()
        roles = _extract_roles(claims)
        if required not in roles and "admin" not in roles:
            raise AuthError(f"required role '{required_role}' not present")

    required_scope = _required_scope_for_role(config, required_role)
    if required_scope:
        scopes = _extract_scopes(claims)
        if required_scope not in scopes and "rendezvous:*" not in scopes:
            raise AuthError(f"required scope '{required_scope}' not present")

    jti_value = claims.get("jti")
    jti = jti_value.strip() if isinstance(jti_value, str) else ""
    if config.require_jti and not jti:
        raise AuthError("required claim 'jti' not present")

    if jti:
        replay_key = f"{claims.get('sub', '')}:{jti}"
        is_new = REPLAY_CACHE.check_and_store(
            replay_key,
            ttl_seconds=config.jti_replay_ttl_seconds,
            max_entries=config.jti_replay_cache_max_entries,
        )
        if not is_new:
            raise AuthError("replayed token rejected")

    return claims


def authorize_websocket(websocket: WebSocket, config: AuthConfig, required_role: str) -> tuple[bool, int, str]:
    token = extract_websocket_token(websocket)

    if not token:
        if config.require_signed_tokens:
            return False, 4401, "missing access token"
        if not config.allow_legacy_no_token:
            return False, 4401, "token required by policy"
        return True, 1000, "ok"

    try:
        validate_access_token(token, config, required_role=required_role)
    except AuthError as ex:
        return False, 4403, str(ex)

    return True, 1000, "ok"
