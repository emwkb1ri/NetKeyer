#!/usr/bin/env bash
set -euo pipefail

REPO_DIR="${NETKEYER_RENDEZVOUS_DIR:-/opt/rendezvous_services}"
COMPOSE_BASE="${NETKEYER_COMPOSE_BASE:-docker-compose.yml}"
COMPOSE_NGINX="${NETKEYER_COMPOSE_NGINX:-docker-compose.nginx.yml}"

cd "$REPO_DIR"
docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_NGINX" exec -T nginx nginx -s reload
