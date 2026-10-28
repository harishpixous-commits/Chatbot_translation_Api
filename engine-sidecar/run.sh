#!/usr/bin/env bash
# Dev launcher: localhost-only by design. See server.py docstring.
set -euo pipefail
cd "$(dirname "$0")"
exec uvicorn pixous_engine.server:app --host 127.0.0.1 --port 8765 --workers 1
