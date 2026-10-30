# pixous-i18n.js — dynamic whole-site translation SDK

One script tag, no framework dependency. Walks the DOM (text nodes, title/alt/
aria-label, input placeholders, button labels, SVG `<text>`, open shadow roots,
same-origin iframes), batches strings, and translates via YOUR OWN backend
(default `POST /api/ai/translation/batch` from Pixous.AI.AspNetCore). Never
calls an external provider.

```html
<script src="/pixous-i18n.js"></script>
<script>
  PixousI18n.init({ sourceLanguage: "en" });
  // e.g. from your language dropdown:
  PixousI18n.setLanguage("ta");       // whole UI -> Tamil, dynamic nodes too
  PixousI18n.setLanguage("en");       // exact originals restored
</script>
```

Behavior, tested by Playwright against the real engine (30/30 + failure case):
- MutationObserver (debounced) translates dynamically added content — the same
  i18n + MutationObserver stack the author uses in his own projects.
- In-memory cache by default; localStorage persistence is OPT-IN
  (`persistCache: true`) and hosts enabling it MUST call `PixousI18n.clearCache()`
  on logout/account switch. Persisted entries are namespaced by
  version+tenant+language and expire after 7 days.
- Rapid language switching is race-safe: stale responses and aborted chains
  never touch the DOM.
- On endpoint failure the originals stay in place and
  `<html data-pixous-i18n="error">` is set.

Privacy rails (do not weaken):
- Never reads/transmits input values (except button/submit/reset labels),
  textarea content, contenteditable content, or anything under
  `[data-no-translate]`, `[translate="no"]`, `.no-translate`,
  `<script>/<style>/<code>/<pre>/<textarea>/<template>` subtrees.
- password/hidden/file inputs are skipped entirely.
- Cross-origin iframes and closed shadow roots are inaccessible and skipped.

Translation units: an element containing ONLY text is translated as one
sentence (full context). Mixed inline markup (text + <b>/<span>) is translated
per text node with leading/trailing whitespace preserved, so words never glue
across element boundaries; per-node phrasing may still be less fluent than a
whole-sentence translation for heavily interleaved markup.

Known limits: closed shadow roots and cross-origin iframes cannot be
translated; per-element attribute originals are snapshotted at first scan, so
an app that rewrites attributes after translation owns re-triggering.

### New options (v1.2 hardening)
- `markerRetry` (bool, default true since 2026-10-27) - bounded one-pass retry of rejected marker skeletons with a fresh prefix. Alone-vs-batched survival measured identical, so the retry is the only measured mitigation and ships ON; set false to opt out.
- `markerRetryMax` (int, default 10) - max skeletons retried per pass. Bounds measured: zero extra calls on clean passes, exactly one bounded fetch on lossy passes, no duplicate storms.
- `failedBlockMaxAttempts` (int, default 2) - a repeatedly failing block is stranded (never auto re-fetched) only after this many failed attempts; per-language keys; session-scoped; `clearCache()` resets.
- `roundTripCheck` (bool, default true) - back-translate short blocks and fail closed on opposite-meaning polarity flips (sign in/out, on/off, enable/disable, ...). Token-bearing labels are checked with tokens stripped; unverifiable checks fail closed. Measured 1 true flag / 0 false positives over 19 strings x 5 languages on the real engine.
- `roundTripMaxChars` (int, default 60) - only blocks at or under this length are round-trip checked.
- `roundTripLanguages` (array, default all supported) - target languages the check runs for.
- `echoCheckLanguages` (string[], default ["ta","hi","te","ml","kn"]) - targets where echo retention is checked.
- `echoLatinThreshold` (number, default 0.6) - Latin fraction above which a translation is treated as an untranslated echo and rejected.
- `markFactory` (fn) - testing seam for deterministic marker generation.
- `echoAllowlist` (Array<string|RegExp>, default []) - brand/product names exempt from the echo Latin fraction. URLs and emails are always exempt. The echo guard is a heuristic fail-closed check (skeletons AND plain text/attrs), not semantic QA.
- `interpolationPatterns` (RegExp[], default covers `{{x}}`, `${x}`, `%s/%d/%i/%f`, ICU `{name}`) - in-text tokens protected verbatim through the engine; corruption fails closed (original + partial).
- `overrides` (object, default {}) - integrator-supplied exact-match glossary, e.g. `{ "Sign out": { ta: "..." } }`. Applied without an engine call, exempt from the echo heuristic. Use for context-less UI labels the engine gets wrong; nothing is shipped by default.
- `tokenMarkFactory` (fn) - testing seam for token-marker prefixes.

## Script anomaly (candidate)

If the server response includes `scriptAnomalyIndexes` (batch route), the SDK keeps the original text for those items and sets `data-pixous-i18n="partial"`. Servers that do not send the field behave as before. The check is an objective script scan, not a translation-accuracy check. See docs/configuration.md.

### Stranded blocks and "partial" (documented behaviour, tested by `sdk/tests/test-stranded.js`)

A block that fails (server-flagged script anomaly, marker or token corruption, echo) on 2 separate passes (`failedBlockMaxAttempts`) is stranded for the session: it is not re-sent, its original text stays, and every later pass for that language ends `partial` even if the server response of that pass flags nothing (`scriptAnomalyIndexes` empty). `PixousI18n.clearCache()` re-enables it. With a non-deterministic engine, a transient garble seen twice therefore keeps a page `partial` for the rest of the session. This is the designed storm guard, not a transport or aggregation defect (deterministic test, no model); whether 2 attempts is the right limit is a product choice that is not changed here.
