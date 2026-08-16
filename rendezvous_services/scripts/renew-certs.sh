#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${NETKEYER_RENDEZVOUS_DIR:-$(cd "$SCRIPT_DIR/.." && pwd)}"
WEBROOT_DIR="${NETKEYER_CERTBOT_WEBROOT:-/var/www/certbot}"
COMPOSE_BASE="${NETKEYER_COMPOSE_BASE:-docker-compose.yml}"
COMPOSE_NGINX="${NETKEYER_COMPOSE_NGINX:-docker-compose.nginx.yml}"
CERTBOT_BIN="${NETKEYER_CERTBOT_BIN:-/usr/bin/certbot}"

export NETKEYER_RENDEZVOUS_DIR="$REPO_DIR"
export NETKEYER_COMPOSE_BASE="$COMPOSE_BASE"
export NETKEYER_COMPOSE_NGINX="$COMPOSE_NGINX"

if [[ ! -x "$CERTBOT_BIN" ]]; then
  echo "certbot binary not found at $CERTBOT_BIN" >&2
  exit 1
fi

if [[ ! -d "$WEBROOT_DIR" ]]; then
  echo "certbot webroot directory does not exist: $WEBROOT_DIR" >&2
  exit 1
fi

cd "$REPO_DIR"
"$CERTBOT_BIN" renew --non-interactive --webroot -w "$WEBROOT_DIR" --deploy-hook "$SCRIPT_DIR/reload-nginx-certs.sh"
