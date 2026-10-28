# Versioning

Semantic versioning `MAJOR.MINOR.PATCH`. All `Pixous.AI.*` NuGet packages are
versioned **in lockstep** (current: **1.1.0**); `@pixous/ai-suite` (NPM) tracks
the backend contract it targets (1.1.0 ↔ `/api/ai` + envelope + `/api/ai/v1`).

- `1.x` — backward-compatible: new providers, endpoints, options, additive
  envelope fields. Upgrading = bump package, no code changes.
- `2.x` — breaking changes (route/DTO/interface renames). Any 2.x ships with
  a migration note appended here describing exactly what moved.
- Rules: envelope shape never breaks in a minor; provider `ProviderName`
  strings are stable identifiers (renaming one is a major); `AiSuiteOptions`
  gains optional members only in minors.

## 1.1.0 — streaming, settings panel, shared service surface (2026-10-27)

Additive, backward-compatible (1.0.x consumers upgrade by bumping the package):

- **Real incremental SSE streaming** end to end: per-vendor server-sent-events
  readers (OpenAI-compatible `stream:true` deltas, Anthropic
  `content_block_delta`, Gemini `:streamGenerateContent?alt=sse`), MultiLLM
  failover only BEFORE the first emitted token, mid-stream failures surface
  partial output and never retry/fail over (possibly-billed rule). The Angular
  widget fills the reply bubble token by token (`AiChatService.stream`,
  `@Input() streaming` default on, automatic fallback to plain POST only when
  the backend has no stream endpoint). Translation routing: with
  `responseLanguage` set the widget POSTs by default so the configured
  translation provider applies; `[allowUntranslatedStream]="true"` opts into
  model-language streaming.
- **Angular settings panel** (`<pixous-settings-panel>`): password login gate,
  LLM key management with honest states (untested / ok / invalid / denied /
  cooling / locked - a key shows ok only after the vendor has accepted it),
  project service keys, voice, knowledge uploads, password change.
  Fixed: `AiSettingsService.login()` now actually stores the session token.
- **Provider error sanitization**: key-health `lastError` and test-endpoint
  messages pass through `ProviderErrorSanitizer` (key-shaped tokens redacted,
  concise message extracted from vendor JSON, 160-char cap) - raw vendor
  bodies are never persisted or rendered.
- **Multi-targeting**: the four libraries build for net8.0 + net10.0.
- **Shared service surface `/api/ai/v1/*`** (optional layer for server-to-server
  consumers in any language): project API keys (`pk_…`) issued from the
  settings panel, shown once, stored as SHA-256 hashes; per-key rate limit
  (60/min), usage counters and session namespaces. See
  [service-integration.md](service-integration.md). The existing `/api/ai`
  widget surface is unchanged.
- **429 honored, not evaded**: a rate-limit/quota answer no longer fails over
  to the next key by default (the key cools per `Retry-After` and the vendor's
  answer surfaces). New opt-in `Resilience:AllowQuotaKeyFallback` (default
  false) for deliberate multi-key quota pooling.
- **Stream hardening**: SSE event payloads capped at 1 MB; headers-phase
  timeouts no longer truncate the body stream (lifetime fix).
- The sample no longer ships a committed `ai-suite-settings/` directory (it
  contained a plaintext fake key); settings are runtime state.

## 1.0.1 — security hardening (2026-10-27)

Compatibility changes vs 1.0.0: `RateLimit:Enabled` now defaults to **true**
(safe default — public endpoints spend provider quota); `POST /selection`
requires a settings session; the shipped default settings password is refused
outside Development; persisted secrets are Data Protection encrypted at rest
(owner-only files); undecryptable stored keys surface as `state:"locked"`
instead of being silently cleared; ambiguous provider failures
(timeout/5xx/network) no longer retry or fail over unless the operator opts in
(`Resilience:RetryAmbiguousFailures` / `AllowAmbiguousKeyFallback`, both off by
default). Key status now distinguishes `invalid` (401) from `denied` (403).
New optional `DataProtection:KeyRingDirectory` setting.

Behavior changes vs 1.0.0 (all intentional, documented here as the compatibility contract):

1. **POST /api/ai/selection now requires a ⚙ settings session token** when the
   settings panel is enabled (401 `UNAUTHORIZED` otherwise). Admin UIs must log
   in via `POST /settings/login` and send `X-Settings-Token` (the Angular
   `AiSettingsService` already holds it). GET /selection stays public.
2. **Persisted secrets are encrypted at rest** (ASP.NET Core Data Protection,
   `v1:enc:` prefix in `settings.json`). Existing plaintext 1.0.0 stores load
   fine and are encrypted on next save. Hosts with ephemeral/multi-instance
   deployments must configure a shared data-protection key ring, or stored keys
   are dropped with a warning and must be re-entered.
3. **Secure first setup**: outside Development, the shipped default password
   ("pixous") is REFUSED at login until a real password is set via
   `AiSuite__Settings__Password` (env / User Secrets / Key Vault). Development
   keeps the old behavior plus a loud startup warning.
4. **`Settings:MinPasswordLength` default 4 → 8.**
5. **No automatic retry of possibly-billed provider calls.** Timeouts, 5xx and
   network failures are ambiguous (the provider may have processed the prompt)
   and are no longer retried; only HTTP 429 (a definitive rejection) is.
   Opt back in with `Resilience:RetryAmbiguousFailures=true`.
6. **MultiLLM no longer falls through to the next key after ambiguous errors**
   (timeout/5xx/network) — it stops with a clear error instead of re-sending a
   possibly-billed prompt to another vendor. Fallback after definitive 4xx
   rejections is unchanged. Opt back in with
   `Resilience:AllowAmbiguousKeyFallback=true`.
7. `.gitignore` now excludes `ai-suite-settings/`.

No route, DTO or interface renames; envelope shape unchanged.
