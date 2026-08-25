#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  capture-relay-latency-data.sh --scenario <name> --run <id> [options]

Required:
  --scenario <name>            Scenario label (examples: baseline-49921, nginx-49922)
  --run <id>                   Run label (examples: 1, 2, 3)

Options:
  --duration-seconds <n>       Capture duration in seconds (default: 60)
  --output-root <dir>          Root output directory (default: ./measurements/relay-latency)
  --health-url <url>           Health probe URL (default: https://127.0.0.1/health)
  --health-insecure            Use curl -k for TLS probe (default: enabled)
  --health-interval-ms <n>     Health probe interval in ms (default: 1000)
  --compose-base <file>        Compose base file (default: docker-compose.yml)
  --compose-nginx <file>       Compose nginx overlay file (default: docker-compose.nginx.yml)
  --services <csv>             Comma-separated compose services for logs (default: rendezvous,relay,nginx)

Examples:
  ./scripts/capture-relay-latency-data.sh --scenario baseline-49921 --run 1
  ./scripts/capture-relay-latency-data.sh --scenario nginx-49922 --run 1 --duration-seconds 90
EOF
}

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command not found: $1" >&2
    exit 1
  fi
}

percentile_from_file() {
  local file="$1"
  local pct="$2"

  local count
  count=$(wc -l < "$file" | tr -d ' ')
  if [[ "$count" -le 0 ]]; then
    echo ""
    return 0
  fi

  local idx
  idx=$(awk -v n="$count" -v p="$pct" 'BEGIN { i = int((p/100.0)*n + 0.999999); if (i < 1) i = 1; if (i > n) i = n; print i }')
  awk -v i="$idx" 'NR==i { print $1; exit }' "$file"
}

SCENARIO=""
RUN_ID=""
DURATION_SECONDS=60
OUTPUT_ROOT="./measurements/relay-latency"
HEALTH_URL="https://127.0.0.1/health"
HEALTH_INSECURE=1
HEALTH_INTERVAL_MS=1000
COMPOSE_BASE="docker-compose.yml"
COMPOSE_NGINX="docker-compose.nginx.yml"
SERVICES_CSV="rendezvous,relay,nginx"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --scenario)
      SCENARIO="$2"
      shift 2
      ;;
    --run)
      RUN_ID="$2"
      shift 2
      ;;
    --duration-seconds)
      DURATION_SECONDS="$2"
      shift 2
      ;;
    --output-root)
      OUTPUT_ROOT="$2"
      shift 2
      ;;
    --health-url)
      HEALTH_URL="$2"
      shift 2
      ;;
    --health-insecure)
      HEALTH_INSECURE=1
      shift
      ;;
    --health-interval-ms)
      HEALTH_INTERVAL_MS="$2"
      shift 2
      ;;
    --compose-base)
      COMPOSE_BASE="$2"
      shift 2
      ;;
    --compose-nginx)
      COMPOSE_NGINX="$2"
      shift 2
      ;;
    --services)
      SERVICES_CSV="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$SCENARIO" || -z "$RUN_ID" ]]; then
  echo "--scenario and --run are required." >&2
  usage
  exit 1
fi

if ! [[ "$DURATION_SECONDS" =~ ^[0-9]+$ ]] || [[ "$DURATION_SECONDS" -le 0 ]]; then
  echo "--duration-seconds must be a positive integer." >&2
  exit 1
fi

if ! [[ "$HEALTH_INTERVAL_MS" =~ ^[0-9]+$ ]] || [[ "$HEALTH_INTERVAL_MS" -le 0 ]]; then
  echo "--health-interval-ms must be a positive integer." >&2
  exit 1
fi

require_cmd docker
require_cmd curl
require_cmd awk
require_cmd sort

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_DIR"

if [[ ! -f "$COMPOSE_BASE" ]]; then
  echo "Compose base file not found: $COMPOSE_BASE" >&2
  exit 1
fi

if [[ ! -f "$COMPOSE_NGINX" ]]; then
  echo "Compose nginx file not found: $COMPOSE_NGINX" >&2
  exit 1
fi

UTC_STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="$OUTPUT_ROOT/${UTC_STAMP}_${SCENARIO}_run${RUN_ID}"
mkdir -p "$RUN_DIR"

IFS=',' read -r -a SERVICES <<< "$SERVICES_CSV"
SERVICE_ARGS=()
for svc in "${SERVICES[@]}"; do
  trimmed="$(echo "$svc" | xargs)"
  if [[ -n "$trimmed" ]]; then
    SERVICE_ARGS+=("$trimmed")
  fi
done

if [[ "${#SERVICE_ARGS[@]}" -eq 0 ]]; then
  echo "No services configured for log capture." >&2
  exit 1
fi

cat > "$RUN_DIR/metadata.txt" <<EOF
scenario=$SCENARIO
run_id=$RUN_ID
utc_start=$UTC_STAMP
duration_seconds=$DURATION_SECONDS
health_url=$HEALTH_URL
health_interval_ms=$HEALTH_INTERVAL_MS
compose_base=$COMPOSE_BASE
compose_nginx=$COMPOSE_NGINX
services=${SERVICE_ARGS[*]}
repo_dir=$REPO_DIR
EOF

{
  echo "==== uname ===="
  uname -a || true
  echo
  echo "==== docker version ===="
  docker version || true
  echo
  echo "==== docker compose version ===="
  docker compose version || true
} > "$RUN_DIR/environment.txt"

{
  echo "==== compose ps ===="
  docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_NGINX" ps || true
} > "$RUN_DIR/compose-state.txt"

docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_NGINX" logs --no-color --timestamps "${SERVICE_ARGS[@]}" > "$RUN_DIR/service-logs-pre.txt" 2>&1 || true

docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_NGINX" logs --no-color --timestamps --follow "${SERVICE_ARGS[@]}" > "$RUN_DIR/service-logs-during.txt" 2>&1 &
LOG_PID=$!

echo "timestamp_utc,http_code,total_time_s,curl_exit" > "$RUN_DIR/health-samples.csv"

default_flags=(--silent --show-error --output /dev/null --write-out "%{http_code} %{time_total}")
if [[ "$HEALTH_INSECURE" -eq 1 ]]; then
  default_flags=(-k "${default_flags[@]}")
fi

END_AT=$(( $(date +%s) + DURATION_SECONDS ))
INTERVAL_S="$(awk -v ms="$HEALTH_INTERVAL_MS" 'BEGIN { printf "%.3f", ms/1000.0 }')"

while [[ $(date +%s) -lt "$END_AT" ]]; do
  TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  set +e
  RESPONSE="$(curl "${default_flags[@]}" "$HEALTH_URL" 2>/dev/null)"
  RC=$?
  set -e

  CODE="000"
  TIME_TOTAL="0"
  if [[ $RC -eq 0 ]]; then
    CODE="$(echo "$RESPONSE" | awk '{print $1}')"
    TIME_TOTAL="$(echo "$RESPONSE" | awk '{print $2}')"
  fi

  echo "$TS,$CODE,$TIME_TOTAL,$RC" >> "$RUN_DIR/health-samples.csv"
  sleep "$INTERVAL_S"
done

kill "$LOG_PID" >/dev/null 2>&1 || true
wait "$LOG_PID" 2>/dev/null || true

docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_NGINX" logs --no-color --timestamps "${SERVICE_ARGS[@]}" > "$RUN_DIR/service-logs-post.txt" 2>&1 || true

awk -F',' 'NR>1 && $3+0>0 { print $3 }' "$RUN_DIR/health-samples.csv" | sort -n > "$RUN_DIR/health-times-sorted.txt"

COUNT=$(wc -l < "$RUN_DIR/health-times-sorted.txt" | tr -d ' ')
P50=""
P95=""
P99=""
MAX=""
if [[ "$COUNT" -gt 0 ]]; then
  P50="$(percentile_from_file "$RUN_DIR/health-times-sorted.txt" 50)"
  P95="$(percentile_from_file "$RUN_DIR/health-times-sorted.txt" 95)"
  P99="$(percentile_from_file "$RUN_DIR/health-times-sorted.txt" 99)"
  MAX="$(awk 'END { print $1 }' "$RUN_DIR/health-times-sorted.txt")"
fi

cat > "$RUN_DIR/summary.txt" <<EOF
scenario=$SCENARIO
run_id=$RUN_ID
samples=$COUNT
health_p50_seconds=$P50
health_p95_seconds=$P95
health_p99_seconds=$P99
health_max_seconds=$MAX
run_dir=$RUN_DIR
EOF

cat <<EOF
Capture complete.
Run directory: $RUN_DIR
Summary file:  $RUN_DIR/summary.txt
Next step: append keying latency observations into $RUN_DIR/keying-latency-notes.csv
EOF

echo "timestamp_utc,latency_ms,source,notes" > "$RUN_DIR/keying-latency-notes.csv"
