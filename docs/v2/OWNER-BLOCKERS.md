# Owner-decision blockers (verified, with sources)

## 1. Official AI4Bharat model weights - gated access (OPTIONAL upgrade, NOT a blocker since 2026-10-27 19:23)
UPDATE: default setup uses the public adalat-ai CT2 ports (no account needed,
verified working in six languages). Accepting this gate is now an OPTIONAL
provenance upgrade (official weights + possible self-conversion), not a
requirement. Original details kept below.
The package's setup.sh downloads IndicTrans2 weights from the official repo:
https://huggingface.co/ai4bharat/indictrans2-en-indic-dist-200M

Verified 2026-10-27:
- HF API: `gated: "auto"`, `private: false`, license MIT (card + page badge).
- Anonymous file fetch returns HTTP 401 with verbatim message:
  "Access to model ai4bharat/indictrans2-en-indic-dist-200M is restricted.
   You must have access to it and be authenticated to access it. Please log in."
  (x-error-code: GatedRepo)
- HF documentation (https://huggingface.co/docs/hub/models-gated): with
  AUTOMATIC approval, "any user can access your model once they've shared
  their personal information with you" - i.e. log in, click agree once,
  access is granted immediately (no manual approval wait).

Owner action needed: create/log into a Hugging Face account, open the model
page, accept the gate once (shares name/email with AI4Bharat), then provide
an access token to the machine running setup.sh. No cost. MIT license covers
commercial use; our package never redistributes the weights (setup downloads
them on the target machine), so no redistribution rights are needed.

Note: the model card now recommends the newer "Indic-Translate"
(https://huggingface.co/bodhan-ai/indic-translate) as better than IndicTrans2.
Evaluating a swap is a separate decision; the current engine is fully working.

## 2. Native-speaker quality sign-off (six languages)
Automated evaluation: FLORES-200 dev (n=50/language, official tarball,
sacrebleu chrF vs human references) - direct avg 63.9, pivot avg 54.0.
Known engine-quality case (measured): occasional source-word retention -
the model sometimes leaves an individual English word untranslated inside an
otherwise translated sentence. Marker/page integrity is unaffected (SDK
fails closed on any marker anomaly). Needs native review for production
acceptance: en/ta/hi/te/ml/kn.

## 3. Production chat LLM deployment decision (MINIMUM owner decision: host budget only)
Wiring proven end-to-end locally TWICE: SmolLM2-135M (fixture) and
Qwen3-0.6B Q8_0 (official Qwen/Qwen3-0.6B-GGUF, rev 23749fef, apache-2.0,
no account needed) served the full production system prompt through
.NET /api/ai/chat -> OpenAiCompatible provider -> llama-cpp server.
Measured resource boundary (2026-10-27, 2 GB RAM sandbox, CPU):
- 0.6B Q8_0 boots and serves at n_ctx 2048; OOM-killed at n_ctx 4096.
- 0.6B quality is NOT deployable: EN instruction-following OK, ta/hi factual
  answers measured wrong. Think-tag leak fixed provider-side (ThinkTagFilter).
MINIMUM next decision - RESOLVED 2026-10-27 21:22 (owner, WhatsApp): the target machine is his EXISTING LAPTOP WITH 16 GB RAM; no purchase needed for the 4B path. Setup path prepared: chat-sidecar/setup-chat.{sh,ps1} + start-chat.{sh,ps1} + docs/v2/RUN-CHAT-ON-LAPTOP.md (OS CONFIRMED WINDOWS 21:23 WhatsApp; Windows-first runbook in docs/v2/RUN-CHAT-ON-LAPTOP.md; CPU/GPU unknown - performance estimates labeled; on-host validation battery in the runbook is REQUIRED before real use; 4B quality still untested until that battery passes).

Previous decision framing (kept for reference):
pick a host with enough RAM for a capable model. NO existing user-authorized
host/compute route was found in the project or recorded context (memory +
repo checked 2026-10-27: no servers, cloud accounts or deploy targets on
record; the module was never deployed).

Grounded options (sizing math shown; LABEL: host totals are ESTIMATES derived
from measured sandbox overhead + published file sizes, not measured on a real
host; model QUALITY at 4B/8B is UNTESTED locally - they do not fit the 2 GB
sandbox - so compute alone is not a quality promise):
- ~8 GB RAM (estimate): Qwen3-4B-Instruct-2507 Q4_K_M. Weights 2.33 GiB
  (observed x-linked-size 2497280736 on bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF).
  Runtime overhead measured on 0.6B here: ~1.1-1.2 GB on top of weights at
  n_ctx 4096 (OOM at 1.82 GB anon-rss with 639 MB weights); 4B KV is larger
  (~0.3-0.6 GB at n_ctx 4096 by layer math) -> ~4-5 GB working set, plus OS +
  .NET + sidecar -> ~8 GB total with headroom. Vendor benchmarks (model card,
  not locally verified): MultiIF 70.8, MMLU-ProX 65.1, INCLUDE 67.8 (INCLUDE
  covers Indic languages); 256K native context (card notes OOM -> reduce ctx).
- ~16 GB RAM (estimate): Qwen3-8B Q4_K_M (4.68 GiB weights, observed x-linked-size 5027784224) for multilingual
  headroom; same overhead math scaled.
- No purchase: stay on KnowledgeBase/Echo provider (works today, no LLM).
Quality verification on the chosen host BEFORE production use is part of the
decision: boot the model, run the same chat test battery we ran at 0.6B
(EN instruction-following + ta/hi factual probes), then decide.
No code change is needed for any of these; the model path is config.
