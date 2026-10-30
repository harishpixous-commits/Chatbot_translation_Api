# Security

## Provider error sanitization

Vendor error bodies can echo the caller's key back or dump structured JSON.
Every error recorded for key health (`lastError`) and returned by the key-test
endpoint passes through `ProviderErrorSanitizer`: key-shaped tokens are
replaced with `[redacted]` regardless of vendor behavior, the human message is
extracted from JSON instead of truncating it, and the result is capped at 160
characters. The full vendor body is never persisted or rendered.

## Guarantees by design

- Keys never in C#, Angular, Git, logs, or API responses (pointers +
  `IConfiguration` at startup; `/info` returns booleans + config paths only).
- Browser calls only YOUR domain (`/api/ai/*`); upstream hosts are unreachable
  from the frontend. Keys enter ONLY through the password-gated ⚙ panel
  (authenticated HTTPS, write-only) or server-side config (User Secrets / env /
  Key Vault / AWS Secrets Manager) — never through code, appsettings, or the
  public chat surface.
- Safe errors: envelope codes (`VALIDATION_ERROR`, `PROVIDER_NOT_CONFIGURED`,
  `*_UNAVAILABLE`, `INTERNAL_ERROR`) with `traceId`; upstream bodies truncated
  to 300 chars; no stack traces or internals leak.
- Structured logs carry provider/operation/duration/outcome — never prompts,
  keys, or `Authorization` headers. Prompt-injection note: system instructions
  stay server-side (`ChatDefaults:SystemPrompt`); treat user input as data,
  never as instructions; render markdown as sanitized HTML in your UI.

## ⚙ Settings panel contract

- **Gate**: `POST /settings/login` (password → 8h token) — wrong password 401;
  5 failures/IP → lockout (15 min, then 60 s retry). Every other settings
  route requires `X-Settings-Token` (or `Bearer`) → else 401 `UNAUTHORIZED`.
- **PBKDF2** password hash persisted in the store; `Settings:Password` only
  seeds the first run (`passwordSource: config` → `stored` after a change).
- **Raw keys are write-only**: they go in on authenticated HTTPS and come back
  only as `apiKeyMasked` (`sk-a…1234`) + `hasKey`. Blank/omitted key on any
  save keeps the stored secret — masked values can never round-trip into a
  real key. Voice keys behave the same (`sttKeyMasked`/`ttsKeyMasked`).
- **Disk**: persisted secrets are ENCRYPTED AT REST with ASP.NET Core Data
  Protection (`v1:enc:` values in `ai-suite-settings/settings.json`;
  `AllowPersistKeys=false` keeps keys memory-only instead). The data-protection
  key ring is host-local by default — multi-instance/ephemeral hosts must
  configure a shared key ring (`PersistKeysToStackExchangeRedis`, blob storage,
  etc.) and back it up with the settings directory; the key ring IS the backup.
  If a stored secret can't be decrypted (different/missing key ring), the
  ciphertext is PRESERVED and the key enters a visible **locked** state
  (excluded from use, shown in the panel, surfaced via `state: "locked"`):
  re-entering the secret in ⚙ unlocks it. Deleting a key is a separate,
  deliberate operator action — nothing is silently dropped. Keep the
  directory out of Git anyway (`.gitignore` excludes it since 1.0.1) and out
  of backups you don't control.
- **Key ring separated and permission-protected (not itself encrypted)**: the master keys are kept APART from the
  encrypted settings (framework default location, never inside
  `Settings:StoreDirectory` — the library warns loudly if configured
  otherwise). Set `AiSuite:DataProtection:KeyRingDirectory` for an explicit
  location: the library creates it owner-only (`0700`, key files `0600` on
  Unix; ACLs are the host's job on Windows) and fixes world-readable key
  files with a warning. The settings directory and `settings.json` are also
  written owner-only. Owner-only permissions are NOT encryption of the key
  ring itself — use host-side DP encryption (ProtectKeysWith*/Key Vault)
  where that is required. Multi-instance/ephemeral hosts: point the ring at
  shared, backed-up storage (Redis/blob/Key Vault via the host's own
  `AddDataProtection`).
- **Live provider switching is admin-only**: `POST /selection` requires a valid
  settings session token (since 1.0.1); `GET /selection` and `/info` stay
  public but expose only provider NAMES and configured/not-configured flags —
  never endpoints, secrets or secret values.
- **Public chat/voice/translation endpoints spend your provider quota by
  design**: `/chat`, `/chat/stream`, `/translation/*` and `/voice/stt|tts` are
  unauthenticated so any visitor's widget works — every call bills the
  operator's keys. Since 1.0.1 the built-in per-caller-IP rate limiter is ON
  BY DEFAULT (`AiSuite:RateLimit:Enabled`, 20 req/min per IP) so a fresh
  install is RATE-limited against casual abuse out of the box; disable it
  only when the host already front-ends its own limiter/WAF. This is NOT a
  monetary cap: many client IPs, token volume per call, retries and
  multi-instance deployments all bypass a per-IP limit, and behind a proxy
  the host must configure forwarded headers or every visitor partitions
  under the proxy's IP. Real spend control = provider-side budgets/alerts
  plus host auth (`Security:RequireAuthorization`) for non-public
  deployments.
- **Secure first setup**: outside Development the shipped default password is
  refused until `AiSuite__Settings__Password` is set by the operator.
- **Billing-safe failure handling**: calls that may already have been billed
  (timeout/5xx/network) are never retried and never fall through to the next
  key by default — the error says so and names the opt-ins
  (`Resilience:RetryAmbiguousFailures`, `Resilience:AllowAmbiguousKeyFallback`).
  These are deliberate operator configuration choices; they are off by default
  and opting in accepts possible duplicate billing.
- **Failure semantics are honest**: a 401 cools the key as `invalid`
  (credentials rejected); a 403 cools it as `denied` (the provider refused
  access — permission, billing or region — which is NOT the same as a wrong
  key); a 429 cools it per `Retry-After` as `cooling` (a rate/quota signal,
  NOT proof the call was free — the provider may still bill it).
  Any provider call — even one answered 4xx — may appear on the provider's
  bill; the dashboard, not the status code, is the final word on billing.
- **Failover is for AVAILABILITY, never for quota evasion**: MultiLLM rotates
  keys so one revoked, misconfigured or provider-rejected key doesn't take
  the chat down. It must NOT be used to get around a provider's account
  limits: keys on the same account share that account's quota, and adding
  keys (same or other accounts) to dodge a provider's limits violates the
  provider's terms. So a 429 (rate limit / quota exhausted) does NOT fail
  over by default — the key cools per `Retry-After` and the vendor's answer
  is surfaced to the caller unchanged. Spending a different key's quota on a
  429 is quota evasion, not availability; the operator opt-in
  `Resilience:AllowQuotaKeyFallback` exists only for deliberate multi-key
  quota pooling. When the account is out of quota, wait, upgrade the plan
  or reduce traffic — that is the correct response to a 429.
- **Defaults grant no spend authority**: the resilience opt-ins
  (`RetryAmbiguousFailures`, `AllowAmbiguousKeyFallback`) are OFF by default.
  Enabling them is a deliberate operator decision that increases the number
  of possibly-billed calls (a timed-out request may have been processed and
  billed already). Their defaults are the safe values; the library never
  retries or doubles a provider call on its own.
- **Logs**: store operations log outcomes only — no keys, no passwords, no
  prompts. Key-test failures store a `SecretFree` upstream message.
- **Client**: `AiSettingsService` holds the token in `sessionStorage` only,
  clears it on any 401 and emits `unauthorized$` so your UI re-opens the gate.

## Mandatory host checklist

Since 1.2.0 the module wires auth, rate limiting and exact-origin CORS from
configuration in MapAiSuiteEndpoints (CORS -> auth -> rate limit order);
no manual UseCors/UseAuthentication/UseAuthorization/UseRateLimiter calls:

```jsonc
"AiSuite": {
  "Security": {
    "RequireAuthorization": true
    // and EITHER "ApiKeys": ["<from User Secrets/env/Key Vault>"]  (built-in X-API-Key scheme)
    // OR your own AddAuthentication(JwtBearerDefaults...) OIDC scheme.
    // Startup FAILS CLOSED when RequireAuthorization is on with no scheme.
  },
  "RateLimit": { "Enabled": true },              // default since 1.0.1
  "Cors": {
    "Enabled": true,
    "AllowedOrigins": ["https://app.mycompany.com"]  // exact origins only; '*' is rejected
  }
}
// + guard POST /api/ai/selection with an admin role/policy
```

```typescript
provideHttpClient(withInterceptors([aiSuiteAuthInterceptor(() => tokenService.getToken())]))
```

Prefer Managed Identity (`DefaultAzureCredential`) so there is no key to leak;
rotate any key that ever touches logs, chat history, or the browser.
Pre-commit: `appsettings.*` must contain only `ApiKeyConfigKey` pointers
(root `.gitignore` covers secrets, `.env`, `bin/`, `obj/`, `node_modules/`).

## Public chat request boundary (caller-controlled fields)

- `history` System-role messages from the caller are **dropped by default**
  (`ChatDefaults:AllowClientSystemMessages=false`) so a public caller cannot replace the
  server-side system prompt. Trusted host apps that send their own system message set it to
  `true`; the provider then uses that message INSTEAD of `ChatDefaults:SystemPrompt`.
  This does not authenticate callers or limit spend.
- `temperature` must be a finite number in `[0, ChatDefaults:MaxClientTemperature]` (default 2.0),
  otherwise HTTP 400 `VALIDATION_ERROR`.
- `provider` per-request selection keeps its documented precedence (per-request, then runtime
  selection, then configuration): any registered provider can be chosen. Restrict it with
  `ChatDefaults:AllowedClientProviders` (list; empty list denies every client choice).
- Translation `provider` per-request selection follows the same rule: restrict it with the
  top-level `AllowedClientTranslationProviders` (list; empty denies every client choice; default
  null = any registered translation provider).
- Limits on public routes: `/translation/batch` items and `/translation/detect-language` text are
  capped at `ChatDefaults:MaxMessageChars` (default 4000) like `/translation/translate`;
  `/voice/stt` uploads are capped by `Settings:MaxAudioBytes` (default 10 MB, rejected before the
  body is buffered); `/voice/*` shares the per-IP rate limit with `/chat`. Failed-login tracking
  keeps at most 50,000 callers in memory and never evicts an active lockout.
- `OpenAiCompatible:ExtraSamplingParameters` (operator config, not caller input): optional
  `top_p`, `top_k`, `min_p`, `seed` for local OpenAI-compatible servers; unknown/reserved/out-of-range
  entries fail closed. Remote vendors should leave it unset.
