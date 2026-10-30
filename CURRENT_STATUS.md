# Current status (2026-10-30) - branch source-fixes/2026-10-30-chatbot-quality

This branch is the updated working code on top of upstream `main` (900fea5). It is NOT merged, NOT deployed, and NOT accepted as release-ready.
Everything below was observed locally (2 GB Linux host, int8 self-hosted engine) or on the earlier Windows CI run; nothing here was run against an external translation provider.

## What changed vs main (summary)
- Self-hosted translation sidecar: resource guards, queue/cancel/shutdown handling, bounded chunking, opt-in glossary, structure/token masking, `quality` field with a wrong-script check, `/health` limits.
- .NET: capacity-typed errors (429/503), 413 mapping, long-text splitting (`PixousEngine:MaxItemChars`, default 2000), `scriptAnomaly` / `scriptAnomalyIndexes` in the translate APIs, chat withholds a flagged translation.
- Website SDK: keeps the original text for server-flagged items and ends the page `partial`; stranded-block rule documented; Angular chat widget honest status/errors.
- Docs: docs/configuration.md, docs/api-reference.md, engine-sidecar/README.md, sdk/README.md, docs/v2/quality-known-issues.md.
New defaults are candidates: active only if this branch is merged and deployed.

## Last local test results (scoped)
- .NET unit 250/250 on net8.0 and net10.0; integration 87/87 (net10.0).
- Sidecar suite 44 PASS / 0 FAIL; script-check unit 31/31; Angular widget 19/19; SDK deterministic suites pass (stub/fixture, not real-model proof).
- Real-model batch route run once each for hi, ta, te, ml, kn (single runs, not rates).
- Native Windows: the earlier CI run (workflow_dispatch only) covered older code; this branch has NOT been run on Windows.

## Known failures and open risks (not fixed)
- Semantic regressions: 5 of 10 FAIL (`engine-sidecar/tests/semantic_regressions.py`): "role" -> vessel/character (2), "welcome" greeting (2), "Press" -> newspaper (1). Model output; the failing tests were not loosened.
- The engine output varies between identical requests in one process (12/540 and 4/369 items differed from the majority in two settings; cause not established).
- Long texts can lose content (a 3371-char text lost numbers in one run, 68/69 in another). No check detects dropped content.
- Stray wrong-script tokens (for example Ol Chiki letters inside Tamil output) are flagged, not repaired; output is never edited.
- The SDK real-engine browser suite (`sdk/tests/test.js`) is not stable-green on the real model (page ends `partial` in some runs).
- Own Tamil/Hindi review was by a non-native reader. No native-language review, no native Windows run of this branch.
- First-use (not startup) validation of `MaxItemChars` against the sidecar limits; abbreviation sentence-split limitation (documented).
- A 200 response is not a translation-quality or completeness guarantee.
