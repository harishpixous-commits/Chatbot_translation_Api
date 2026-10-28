# Pixous AI Suite v1.2.0 — chatbot + self-hosted translation

## What's in the box (one coherent version: 1.2.0)
| component | package / dir | tested |
|---|---|---|
| .NET libraries | Pixous.AI.{Abstractions,Core,Providers,AspNetCore}.1.2.0.nupkg | 126/126 unit on net8 + net10, 34/34 integration |
| Angular UI library | pixous-ai-suite-1.2.0.tgz (peer 17/18/19, built+verified on 18) | v1.1 browser checks; unchanged UI code in 1.2.0 |
| Translation engine sidecar | engine-sidecar/ (FastAPI + IndicTrans2 CT2) | curl-verified real output, bounds, auth paths |
| Website SDK | sdk/pixous-i18n.js | 30/30 Playwright checks + failure fallback, real model |
| Evaluation | docs/v2/eval-30-pairs.md | 30/30 directed pairs, FLORES-200 dev n=50, human references |

## Setup order
1. `engine-sidecar/setup.sh` then `engine-sidecar/run.sh` (binds 127.0.0.1:8765).
2. .NET host: set `AiSuite:TranslationProvider = "PixousSelfHosted"` and
   `AiSuite:PixousEngine:Endpoint = "http://127.0.0.1:8765"`. Optional token via
   `PixousEngine:TokenConfigKey` (pointer to config/env, never the token inline).
3. Website: `<script src="/pixous-i18n.js">`, `PixousI18n.init({sourceLanguage:"en"})`,
   wire your language dropdown to `PixousI18n.setLanguage(code)`.
4. Chatbot: unchanged from v1.1 — Angular components + `/api/ai/chat` endpoints,
   provider configurable (Echo/KnowledgeBase/Ollama/OpenAI-compatible).

## Restart / recovery
- Sidecar restart: models reload lazily on first request (seconds per direction
  switch); nothing else is stateful. `setup.sh` re-fetches pinned model revisions
  if the cache is lost.
- SDK: translations re-derive from the server; localStorage cache is namespaced
  by version+tenant+language with 7-day TTL. Call `PixousI18n.clearCache()` on
  logout/account switch; `cache:false` opts out of persistence entirely.
- .NET: settings/keys store persists per v1.1 semantics (file store, protected).

## Security posture (unchanged + new)
- Sidecar binds localhost by default; localhost is NOT auth — set
  PIXOUS_ENGINE_TOKEN + TLS before network exposure. Hard request size bounds.
- SDK never transmits input values (except button labels), textarea or
  contenteditable content, password/hidden inputs, or [data-no-translate] /
  translate="no" / .no-translate subtrees. Responses are JSON; the SDK contains
  no HTML-execution sinks (no innerHTML/eval).
- Retry policy unchanged: ambiguous failures (timeout/5xx/network) never retried
  by default; 429 honored as cooling, never hopped.

## Honest pending / limits
- See engine-sidecar/README.md (weights provenance, pivot note, memory) and
  docs/v2/eval-30-pairs.md (metric limits). Full 997-sentence FLORES run,
  native-speaker review, and a combined .NET+sidecar sample-app demo run are
  NOT done yet.
