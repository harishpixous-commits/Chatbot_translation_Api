# SDK test harness

Deterministic + real-engine browser tests for pixous-i18n.js.

Setup: `npm i playwright-core` and a local Chromium/Chrome headless shell.
Set CHROME_EXE to the browser binary.

- `node server.js` (PORT env, default 8901): demo page + batch proxy.
  FAIL_MODE=1 makes the endpoint return 500; ?mangle=drop|reorder|malformed
  corrupts markers adversarially; ?mangle=padfixture returns deterministic
  padded pseudo-translations (no model needed).
- `test-regression.js` - deterministic: marker padding, stale-check, cached
  second pass, whitespace, structure, dynamic-node race, English restore.
- `test-race.js` - language-switch mid-flight, rapid switching, overlapping
  mutations, clearCache.
- `test-mangle.js` - adversarial marker corruption: drop/reorder/malformed
  all preserve originals + state="partial".
- `test-fail.js` - endpoint 500: originals preserved + state="error"
  (run server with PORT=8902 FAIL_MODE=1).
- `test.js` - full real-engine suite (needs the Pixous engine sidecar on
  :8765): 34 checks incl. real Tamil output, privacy (no password/form/secrets
  transmission), shadow DOM, SVG, skeleton application.
- `shot.js` - captures a full-page Tamil screenshot for pixel review.
