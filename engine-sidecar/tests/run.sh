#!/bin/sh
# Engine-sidecar test harness: main instance + token-configured auth instance.
# Usage: sh tests/run.sh [--slow]   (run from the engine-sidecar directory)
set -e
cd "$(dirname "$0")/.."
export PIXOUS_MODEL_CACHE="${PIXOUS_MODEL_CACHE:-/tmp/hfcache}"
BASE_PORT="${PORT:-8765}"
AUTH_PORT="${AUTH_PORT:-8766}"
python3 -m uvicorn pixous_engine.server:app --host 127.0.0.1 --port "$BASE_PORT" --workers 1 &
MAIN=$!
PIXOUS_ENGINE_TOKEN=test-secret-token python3 -m uvicorn pixous_engine.server:app --host 127.0.0.1 --port "$AUTH_PORT" --workers 1 &
AUTH=$!
cleanup() { kill "$MAIN" "$AUTH" 2>/dev/null || true; }
trap cleanup EXIT
for p in "$BASE_PORT" "$AUTH_PORT"; do
  i=0
  until curl -sf "http://127.0.0.1:$p/health" >/dev/null 2>&1; do
    i=$((i+1)); [ "$i" -gt 60 ] && { echo "server on $p never came up"; exit 2; }
    sleep 2
  done
done
python3 tests/run_sidecar_tests.py "$@"
