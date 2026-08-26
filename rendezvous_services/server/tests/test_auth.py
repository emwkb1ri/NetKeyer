from __future__ import annotations

import time
import unittest

import jwt

from server.auth import AuthConfig, AuthError, authorize_websocket, parse_bearer_token, validate_access_token


class _FakeQueryParams(dict):
    def get(self, key, default=None):
        return super().get(key, default)


class _FakeWebSocket:
    def __init__(self, auth_header: str = "", query_token: str = "") -> None:
        self.headers = {}
        if auth_header:
            self.headers["authorization"] = auth_header
        self.query_params = _FakeQueryParams()
        if query_token:
            self.query_params["access_token"] = query_token


class AuthTests(unittest.TestCase):
    def setUp(self) -> None:
        self.secret = "test-secret"
        self.config = AuthConfig(
            require_signed_tokens=True,
            allow_legacy_no_token=False,
            jwt_secret=self.secret,
            jwt_issuer="issuer-a",
            jwt_audience="netkeyer",
            required_scope_host="rendezvous:host",
            required_scope_client="rendezvous:client",
            jti_replay_ttl_seconds=60,
            jti_replay_cache_max_entries=1000,
            require_jti=True,
        )

    def _token(
        self,
        role: str = "client",
        subject: str = "user-1",
        jti: str = "jti-1",
        scope: str = "rendezvous:client",
    ) -> str:
        now = int(time.time())
        payload = {
            "sub": subject,
            "iat": now,
            "exp": now + 300,
            "iss": "issuer-a",
            "aud": "netkeyer",
            "role": role,
            "scope": scope,
            "jti": jti,
        }
        return jwt.encode(payload, self.secret, algorithm="HS256")

    def test_parse_bearer_token(self) -> None:
        self.assertEqual(parse_bearer_token("Bearer abc"), "abc")
        self.assertEqual(parse_bearer_token("bearer xyz"), "xyz")
        self.assertEqual(parse_bearer_token("Token xyz"), "")

    def test_validate_access_token_accepts_role(self) -> None:
        token = self._token(role="host", scope="rendezvous:host", jti="jti-role-ok")
        claims = validate_access_token(token, self.config, required_role="host")
        self.assertEqual(claims.get("sub"), "user-1")

    def test_validate_access_token_rejects_missing_role(self) -> None:
        token = self._token(role="client", scope="rendezvous:client", jti="jti-role-fail")
        with self.assertRaises(AuthError):
            validate_access_token(token, self.config, required_role="host")

    def test_validate_access_token_rejects_missing_scope(self) -> None:
        token = self._token(role="client", scope="other:scope", jti="jti-scope-fail")
        with self.assertRaises(AuthError):
            validate_access_token(token, self.config, required_role="client")

    def test_validate_access_token_accepts_scope_wildcard(self) -> None:
        token = self._token(role="client", scope="rendezvous:*", jti="jti-scope-wildcard")
        claims = validate_access_token(token, self.config, required_role="client")
        self.assertEqual(claims.get("sub"), "user-1")

    def test_validate_access_token_rejects_missing_jti(self) -> None:
        now = int(time.time())
        payload = {
            "sub": "user-1",
            "iat": now,
            "exp": now + 300,
            "iss": "issuer-a",
            "aud": "netkeyer",
            "role": "client",
            "scope": "rendezvous:client",
        }
        token = jwt.encode(payload, self.secret, algorithm="HS256")
        with self.assertRaises(AuthError):
            validate_access_token(token, self.config, required_role="client")

    def test_validate_access_token_rejects_replayed_jti(self) -> None:
        token = self._token(role="client", scope="rendezvous:client", jti="jti-replay")
        claims = validate_access_token(token, self.config, required_role="client")
        self.assertEqual(claims.get("sub"), "user-1")
        with self.assertRaises(AuthError):
            validate_access_token(token, self.config, required_role="client")

    def test_authorize_websocket_requires_token_when_enabled(self) -> None:
        ws = _FakeWebSocket()
        allowed, code, _ = authorize_websocket(ws, self.config, required_role="client")
        self.assertFalse(allowed)
        self.assertEqual(code, 4401)

    def test_authorize_websocket_allows_legacy_without_token(self) -> None:
        config = AuthConfig(
            require_signed_tokens=False,
            allow_legacy_no_token=True,
            jwt_secret="",
            jwt_issuer="",
            jwt_audience="",
            required_scope_host="",
            required_scope_client="",
            jti_replay_ttl_seconds=0,
            jti_replay_cache_max_entries=0,
            require_jti=False,
        )
        ws = _FakeWebSocket()
        allowed, _, _ = authorize_websocket(ws, config, required_role="client")
        self.assertTrue(allowed)

    def test_authorize_websocket_accepts_query_token(self) -> None:
        token = self._token(role="client", scope="rendezvous:client", jti="jti-query")
        ws = _FakeWebSocket(query_token=token)
        allowed, code, _ = authorize_websocket(ws, self.config, required_role="client")
        self.assertTrue(allowed)
        self.assertEqual(code, 1000)


if __name__ == "__main__":
    unittest.main()
