# Pixous translation engine sidecar (self-hosted, no external provider)

FastAPI service that serves real machine translation on your own host using
IndicTrans2 distilled 200M models in CTranslate2 int8 form. Nothing leaves the
machine: no translation API, no telemetry, no third-party inference call.

## Model provenance (read before redistribution)
Converted CT2 weights, pinned revisions:
- https://huggingface.co/adalat-ai/ct2-rotary-indictrans2-en-indic-dist-200M
  revision `ee04de9a86e42920ac310a0fe5a1505c85671b68`
- https://huggingface.co/adalat-ai/ct2-rotary-indictrans2-indic-en-dist-200M
  revision `524aade4f724dccbc6cd5ffb5e3c5c832a2baee4`
Both repos declare `license: mit` in their model-card metadata (observed) and
ask citation of Gumma et al., NAACL 2025 (RoPE long-context IndicTrans2
variant). Publisher is Adalat AI, an Indian legal-tech non-profit. This is a
THIRD-PARTY conversion: the card declares MIT, but we have not verified Adalat
AI's redistribution rights against AI4Bharat beyond that declaration, and the
underlying checkpoints are the RoPE variant from that paper, not necessarily
bit-identical to ai4bharat's original releases.

Official route status (re-verified 2026-10-27 ~19:22 IST):
- DEFAULT SETUP REQUIRES NO HUGGING FACE ACCOUNT: both adalat-ai repos are
  public (`gated: false`, `license: mit` in API metadata) and their weight
  files fetch anonymously (HTTP 302 -> CDN confirmed for
  en-indic-200m-ct2/ctranslate2_model/model.bin).
- The official ai4bharat model repo remains gated (`gated: auto`, anonymous
  fetch HTTP 401, sha 2cfd0a20057756d756bf3e53528f492afc52f91a), and the
  ai4bharat/BPCC DATASET repo hosting the official Fairseq checkpoint links
  from the GitHub README is ALSO gated (anonymous fetch HTTP 401, sha
  5bde309374f21c39e7c3d505f28ae11f58f2a220). GitHub carries code only (MIT),
  no ungated official weights.
- Author-run public alternative: prajdabre/rotary-indictrans2-en-indic-dist-200M
  (+ indic-en pair), linked from the official GitHub README (RoPE long-context
  variants by an IndicTrans2 co-author): `gated: false`, `license: mit`, rev
  23249dd48107a6207a4ac1fba62789c884458f0f. Different format (not CT2) - not a
  drop-in for this sidecar; would need conversion.
- Optional provenance upgrade (NOT required): owner accepts the ai4bharat gate
  once logged in, then official checkpoints could be self-converted to CT2
  (unverified engineering). The sidecar works fully without it.
- Earlier checks:
- NO official ai4bharat CT2 ports exist on Hugging Face (API search).
- Official weights repo ai4bharat/indictrans2-en-indic-dist-200M is MIT per
  card metadata but GATED (`gated: auto`): download needs an authenticated HF
  account; we did not accept or bypass that gate.
- Self-conversion of the official checkpoints would additionally need a custom
  CTranslate2 spec mapping for IndicTrans2's custom modeling code — unverified
  engineering, not done.
- This package NEVER redistributes weights: setup.sh downloads from the
  publishers at install time.

UNRESOLVED but SCOPED: commercial redistribution clearance of the adalat-ai
ports. This does NOT block source+setup delivery: the package bundles no
weights - every machine downloads them from the publisher at install time,
so no redistribution by us or by the owner occurs in the delivered flow. The
clearance question arises ONLY if the owner later redistributes the weights
themselves (bundling them into a Docker image, installer, zip or appliance
they ship). If that path is ever chosen: review clearance first, or produce
an official-weights conversion after gated access. Not legal advice.

## Indic-to-Indic pairs pivot through English
The CT2 ports ship en-indic and indic-en only. Indic<->Indic pairs are
translated source -> English -> target inside the service. Pivoting is our
implementation choice (a direct CT2 indic-indic port was not found on public
hubs; absence of evidence, not proof none exists). Pivot responses carry
`pivot: true`. Pivot quality is lower than direct pairs.

## Memory and concurrency (observed, not a sizing promise)
- 1 vCPU / 1 GB sandbox: process high-water mark ~1.03-1.06 GB while a model
  was resident. That is ONE observation on ONE box. Production sizing is
  UNVERIFIED.
- Default `PIXOUS_MAX_RESIDENT_MODELS=1`: one model in RAM; direction switches
  evict and reload (seconds per switch).
- All translation is serialized through one lock; concurrent requests queue.

## Security
- Binds 127.0.0.1 by default. LOCALHOST BINDING IS NOT AUTHENTICATION.
- Set `PIXOUS_ENGINE_TOKEN` to require `Authorization: Bearer <token>`, and
  put TLS in front (reverse proxy) before any network exposure.
- Hard request bounds: `PIXOUS_MAX_ITEMS=256`, `PIXOUS_MAX_ITEM_CHARS=2000`,
  `PIXOUS_MAX_TOTAL_CHARS=200000` (413 beyond).

## Run
```bash
./setup.sh            # installs deps + downloads pinned model revisions
./run.sh              # uvicorn on 127.0.0.1:8765
curl -X POST http://127.0.0.1:8765/translate -H 'content-type: application/json' \
  -d '{"source":"en","target":"ta","items":["Welcome to our website."]}'
```

## .NET integration
`Pixous.AI.Providers.PixousSelfHostedTranslationProvider` talks to this
service. Configure:
```json
"AiSuite": {
  "TranslationProvider": "PixousSelfHosted",
  "PixousEngine": { "Endpoint": "http://127.0.0.1:8765", "TokenConfigKey": "AiSuite:ApiKeys:PixousEngine" }
}
```
`TokenConfigKey` is a pointer to config/env/KeyVault, never the token itself.
Detection is a script-level heuristic over the six priority scripts; pass
`sourceLanguage` explicitly for anything important.
