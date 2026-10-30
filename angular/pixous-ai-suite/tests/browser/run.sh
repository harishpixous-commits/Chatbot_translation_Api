#!/bin/sh
# Browser test of the shipped chat widget with a REAL Angular runtime:
# lib sources AOT-compiled via ngc (no partial-link gap), bootstrapped with
# zone.js in headless Chrome against Pixous-shaped API stubs.
# Needs: node_modules installed (npm install), CHROME_EXE set.
set -e
cd "$(dirname "$0")"
mkdir -p .build/pixous
cp ../../src/lib/chat-widget.component.ts ../../src/lib/ai-chat.service.ts ../../src/lib/models.ts .build/pixous/
../../node_modules/.bin/ngc -p tsconfig.json
node server.js &
SPID=$!
trap 'kill $SPID 2>/dev/null' EXIT
sleep 1
node ${WIDGET_SCRIPT:-test-widget.js}
