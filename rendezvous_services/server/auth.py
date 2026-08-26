from __future__ import annotations

from dataclasses import dataclass
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
