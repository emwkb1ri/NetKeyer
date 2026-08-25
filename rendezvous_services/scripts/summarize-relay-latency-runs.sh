#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  summarize-relay-latency-runs.sh [options]

Options:
  --input-root <dir>           Capture root directory (default: ./measurements/relay-latency)
  --baseline-pattern <text>    Scenario match text for baseline (default: baseline-49921)
  --option-pattern <text>      Scenario match text for Option A (default: nginx-49922)
  --budget-ms <number>         Allowed delta p95 budget in ms (default: 5)
  --output <file>              Optional report output file
  -h, --help                   Show this help

Examples:
  ./scripts/summarize-relay-latency-runs.sh
  ./scripts/summarize-relay-latency-runs.sh --budget-ms 5 --output measurements/relay-latency/report.md
EOF
}

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command not found: $1" >&2
    exit 1
  fi
}

INPUT_ROOT="./measurements/relay-latency"
BASELINE_PATTERN="baseline-49921"
OPTION_PATTERN="nginx-49922"
BUDGET_MS="5"
OUTPUT_FILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --input-root)
      INPUT_ROOT="$2"
      shift 2
      ;;
    --baseline-pattern)
      BASELINE_PATTERN="$2"
      shift 2
      ;;
    --option-pattern)
      OPTION_PATTERN="$2"
      shift 2
      ;;
    --budget-ms)
      BUDGET_MS="$2"
      shift 2
      ;;
    --output)
      OUTPUT_FILE="$2"
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

if ! [[ "$BUDGET_MS" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "--budget-ms must be numeric." >&2
  exit 1
fi

require_cmd awk
require_cmd find
require_cmd sort
require_cmd mktemp

if [[ ! -d "$INPUT_ROOT" ]]; then
  echo "Capture directory not found: $INPUT_ROOT" >&2
  exit 1
fi

TMP_CSV="$(mktemp)"
trap 'rm -f "$TMP_CSV"' EXIT

while IFS= read -r summary_file; do
  awk -F'=' '
    BEGIN {
      scenario=""; run_id=""; samples=""; p50=""; p95=""; p99=""; max="";
    }
    $1=="scenario" { scenario=$2 }
    $1=="run_id" { run_id=$2 }
    $1=="samples" { samples=$2 }
    $1=="health_p50_seconds" { p50=$2 }
    $1=="health_p95_seconds" { p95=$2 }
    $1=="health_p99_seconds" { p99=$2 }
    $1=="health_max_seconds" { max=$2 }
    END {
      if (scenario != "" && run_id != "") {
        printf "%s,%s,%s,%s,%s,%s,%s,%s\n", scenario, run_id, samples, p50, p95, p99, max, FILENAME;
      }
    }
  ' "$summary_file" >> "$TMP_CSV"
done < <(find "$INPUT_ROOT" -type f -name summary.txt | sort)

COUNT=$(wc -l < "$TMP_CSV" | tr -d ' ')
if [[ "$COUNT" -eq 0 ]]; then
  echo "No summary.txt files found under $INPUT_ROOT" >&2
  exit 1
fi

REPORT_TMP="$(mktemp)"
trap 'rm -f "$TMP_CSV" "$REPORT_TMP"' EXIT

{
  echo "# Relay Latency Summary"
  echo
  echo "- Input root: $INPUT_ROOT"
  echo "- Baseline pattern: $BASELINE_PATTERN"
  echo "- Option pattern: $OPTION_PATTERN"
  echo "- Budget (delta p95): ${BUDGET_MS} ms"
  echo
  echo "## Run Table"
  echo
  echo "| Scenario | Run | Samples | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | Summary File |"
  echo "|---|---:|---:|---:|---:|---:|---:|---|"

  sort -t',' -k1,1 -k2,2n "$TMP_CSV" | awk -F',' '
    function to_ms(v) {
      if (v == "" || v == "-") return "";
      return sprintf("%.3f", v * 1000.0);
    }
    {
      scenario=$1; run_id=$2; samples=$3; p50=$4; p95=$5; p99=$6; max=$7; path=$8;
      printf "| %s | %s | %s | %s | %s | %s | %s | %s |\n",
        scenario, run_id, samples,
        to_ms(p50), to_ms(p95), to_ms(p99), to_ms(max), path;
    }
  '

  echo
  echo "## Gate Evaluation"
  echo

  awk -F',' -v bpat="$BASELINE_PATTERN" -v opat="$OPTION_PATTERN" -v budget="$BUDGET_MS" '
    function to_ms(v) { return v * 1000.0; }
    BEGIN {
      bsum=0; bcount=0; osum=0; ocount=0;
    }
    {
      scenario=$1; p95=$5;
      if (p95 == "") next;
      if (index(scenario, bpat) > 0) {
        bsum += to_ms(p95);
        bcount += 1;
      }
      if (index(scenario, opat) > 0) {
        osum += to_ms(p95);
        ocount += 1;
      }
    }
    END {
      if (bcount == 0 || ocount == 0) {
        printf "Insufficient data to evaluate gate. baseline_matches=%d option_matches=%d\n", bcount, ocount;
        exit 0;
      }

      bavg = bsum / bcount;
      oavg = osum / ocount;
      delta = oavg - bavg;
      status = (delta <= budget) ? "PASS" : "FAIL";

      printf "- Baseline aggregate p95: %.3f ms (n=%d)\n", bavg, bcount;
      printf "- Option A aggregate p95: %.3f ms (n=%d)\n", oavg, ocount;
      printf "- delta_p95: %.3f ms\n", delta;
      printf "- Gate result: %s (threshold %.3f ms)\n", status, budget;
    }
  ' "$TMP_CSV"
} > "$REPORT_TMP"

cat "$REPORT_TMP"

if [[ -n "$OUTPUT_FILE" ]]; then
  mkdir -p "$(dirname "$OUTPUT_FILE")"
  cp "$REPORT_TMP" "$OUTPUT_FILE"
  echo
  echo "Report written to: $OUTPUT_FILE"
fi
