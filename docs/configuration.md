# Configuration

Single rule: **`appsettings*.json` holds non-secrets ONLY.** Every secret lives
in User Secrets (dev) / environment variables / Key Vault or AWS Secrets
Manager (prod), referenced by an `*ApiKeyConfigKey` pointer.

## Non-secret settings (`AiSuite` section — template: `samples/SampleApi/appsettings.json`)

| Key | Example | Meaning |
|---|---|---|
| `ChatProvider` | `KnowledgeBase` / `Echo` / `OpenAiCompatible` / `MultiLLM` | Chat adapter. `OpenAiCompatible` = one local (llama.cpp/Ollama) or hosted endpoint; `MultiLLM` = API-key vendor chain from the ⚙ panel. Swapped per environment, no code change |
| `TranslationProvider` | `Echo` / `AzureTranslator` / `LibreTranslate` | Translation adapter |
| `OpenAiCompatible:Endpoint` | `https://api.openai.com/v1` | Azure: `{resource}.openai.azure.com/openai/v1`; local: `http://localhost:11434/v1` |
| `OpenAiCompatible:Model` | `gpt-4o-mini` | Model / deployment, swappable per environment |
| `OpenAiCompatible:MaxTokens` / `Temperature` | `512` / `0.7` | Per-call defaults |
| `OpenAiCompatible:ApiKeyConfigKey` | `AiSuite:ApiKeys:Chat` | **Pointer** to the secret, not the secret |
| `OpenAiCompatible:RequireApiKey` | `true` | `false` for keyless local endpoints |
| `OpenAiCompatible:TimeoutSeconds` | `300` for local 4B CPU | Per-provider call timeout; unset = inherits `Resilience:TimeoutSeconds`. Raise ONLY for local inference (llama.cpp/Ollama); hosted vendors keep the global billing guard |
| `AzureTranslator:Endpoint` / `Region` | `https://api.cognitive.microsofttranslator.com` / `southindia` | Region is required by Azure |
| `AzureTranslator:ApiKeyConfigKey` | `AiSuite:ApiKeys:Translator` | **Pointer** to the secret |
| `LibreTranslate:Endpoint` | `https://translate.internal` | Empty = provider stays unregistered |
| `KnowledgeBase:Entries` | `[{Match:[...], Answer:"..."}]` | Config-only FAQ; best-score wins |
| `KnowledgeBase:FallbackAnswer` | `…` | Reply when nothing matches |
| `ChatDefaults:SystemPrompt` | 15-section agent prompt (copy: customization.md → System prompt) | The agent's brain — sent to LLM providers as the first `system` message. `KnowledgeBase` ignores it (FAQ answers come from `KnowledgeBase:Entries`) |
| `ChatDefaults:MaxHistoryMessages` / `MaxMessageChars` | `20` / `4000` | History trim + input cap (cost/abuse control) |
| Translation per-item limit with the self-hosted engine (CANDIDATE: bounded splitting) | `4000` (API) / `2000` (`PixousEngine:MaxItemChars`, must equal sidecar `PIXOUS_MAX_ITEM_CHARS`) | The API accepts up to `MaxMessageChars` per text. A text longer than `PixousEngine:MaxItemChars` is split into ordered sentence-bounded pieces (a . ! ? danda or newline; an over-long sentence is cut at whitespace, else hard-cut), sent as several items of ONE sidecar request and re-joined in order with the original whitespace. Pieces are translated independently, so cross-sentence context is lost at boundaries. Any piece flagged by the script check flags the whole text. Texts at or under the cap are sent unchanged. Contract delta if applied: a 2001-4000 character text returns 200 instead of 400. If `MaxItemChars` is set above the sidecar limit, the sidecar's 413 on a split text is reported as a precise server-side configuration error (`PixousEngine:MaxItemChars` must be <= `PIXOUS_MAX_ITEM_CHARS`, and `PIXOUS_MAX_ITEMS`/`PIXOUS_MAX_TOTAL_CHARS` must allow the pieces) and the caller sees the generic upstream failure, not a validation 400. The sidecar `/health` lists its `limits` (`max_items`, `max_item_chars`, `max_total_chars`); the first time a text is split the provider reads them (5 s bound, success cached for the process lifetime, an unreachable or older `/health` without `limits` is skipped) and fails with the same precise configuration error BEFORE sending any translate request. This is a first-use check, not a startup check: a mismatch is found on the first long text, not at boot. Known splitter limit: an abbreviation followed by a space (for example `Dr. Rao`) is treated as a sentence end when the text exceeds the cap. Decimals, URLs and dotted code with no whitespace after the dot are not cut at the dot.

**Script check coverage (what 'unflagged' means).** `quality.checked` is true only for target languages with a known script. For a target language the check does not know, nothing is checked and `checked` is false; such a result is unverified, not clean. For a checked language the check only detects letters of a wrong *named* script; letters from an unknown-script name can be treated as common, so false negatives exist. An unflagged result is never evidence of a correct translation. |
| Self-hosted engine resource guards (CANDIDATE, not an accepted default) | `PIXOUS_WORKER=1`, `PIXOUS_HARD_SECONDS=120`, `PIXOUS_MAX_RSS_MB=1400`, `PIXOUS_MAX_QUEUE=8`, `PIXOUS_TOKEN_BUDGET=80`, `PIXOUS_CHUNK_GROUP=4` | Sidecar env vars, see `engine-sidecar/README.md`. A busy engine returns HTTP 429 `TRANSLATION_ENGINE_BUSY` with `Retry-After`; a request that hits the time or memory guard returns HTTP 503 `TRANSLATION_RESOURCE_LIMIT`. Other engine failures stay a generic 502. Upstream bodies are never forwarded. Known limit: a valid but token-dense 2000-character item can be refused. `PIXOUS_WORKER=0` removes the hard limits and is unsafe. |
| `Languages:Default` / `Supported` | `en` / `["en","ta","hi","es","fr"]` | **Allow-list** — anything else → 400 `VALIDATION_ERROR`. Never hard-code languages in code |
| `Resilience:TimeoutSeconds` / `MaxRetries` / `BaseDelayMs` | `60` / `2` / `500` | Per-call timeout and retry budget; 4xx never retried. **Local 4B CPU inference can exceed 60s on low-core machines** (measured 85.5s for one short Qwen3-4B Q4_K_M reply on a 2-vCPU Windows CI runner, server warm): keep this global guard for hosted vendors and set `OpenAiCompatible:TimeoutSeconds` for the local-model provider instead |
| `Resilience:RetryAmbiguousFailures` | `false` | `false` = never retry a call that may already be billed (timeout/5xx/network); only 429 (definitive rejection) is retried. `true` restores 1.0.0 retry behavior — accept duplicate-charge risk |
| `Resilience:AllowAmbiguousKeyFallback` | `false` | `false` = MultiLLM tries the next key only after definitive 4xx rejections (except 429); ambiguous errors stop with a clear message. `true` restores 1.0.0 cross-key fallback |
| `Resilience:AllowQuotaKeyFallback` | `false` | `false` = a 429 (rate limit / quota exhausted) is honored: the key cools per `Retry-After` and the vendor's answer surfaces — no spending another key's quota. `true` = deliberate multi-key quota pooling |
| `RateLimit:Enabled` / `PermitLimit` / `WindowSeconds` | `true` / `20` / `60` | Built-in per-caller-IP fixed-window limiter, ON by default since 1.0.1 (public endpoints spend provider quota); `MapAiSuiteEndpoints` wires the middleware itself — do NOT also call `app.UseRateLimiter()` (it would count requests twice). Disable only behind your own limiter/WAF |
| `DataProtection:KeyRingDirectory` | `null` | Optional explicit data-protection key-ring location; created owner-only, must be outside `Settings:StoreDirectory`. Multi-instance/ephemeral hosts: set it (or host-side Redis/blob/Key Vault) |
| `Security:RequireAuthorization` | `false` | Require authenticated user on all AI endpoints; host plugs JWT/OIDC then flips on |

## Secret keys (complete list — TWO)

| # | Config key | Holds | Dev | Prod |
|---|---|---|---|---|
| 1 | `AiSuite:ApiKeys:Chat` | Chat API key (or empty when keyless) | User Secrets / `AiSuite__ApiKeys__Chat` | Key Vault / env / Managed Identity (no key at all) |
| 2 | `AiSuite:ApiKeys:Translator` | Translator subscription key | User Secrets / `AiSuite__ApiKeys__Translator` | Key Vault / env |

```bash
dotnet user-secrets init
dotnet user-secrets set "AiSuite:ApiKeys:Chat" "<chat-key>"
dotnet user-secrets set "AiSuite:ApiKeys:Translator" "<translator-key>"
```

```csharp
// Key Vault provider (consuming project)
builder.Configuration.AddAzureKeyVault(new Uri($"https://{vault}.vault.azure.net/"), new DefaultAzureCredential());
// AWS: Amazon.Extensions.Configuration.SystemsManager / SecretsManager providers work the same way.
```

## ⚙ Settings panel (`AiSuite:Settings` — runtime store)

Config seeds the defaults; the password-gated panel (demo.html ⚙, or your own
admin UI on `AiSettingsService`) can change things live — no restart. Files
live under the store directory (default `ai-suite-settings/`):
`settings.json` (keys, voice, password hash, document index) + `kb/<id>.json`
(parsed chunks).

| Key | Example | Meaning |
|---|---|---|
| `Settings:Enabled` | `true` | Expose `/settings` + `/voice` routes |
| `Settings:Password` | `pixous` | Initial password (seed only — changing it in the panel moves the source to `stored`) |
| `Settings:StoreDirectory` | `ai-suite-settings` | Relative to content root (or absolute) |
| `Settings:AllowPersistKeys` | `true` | Persisted secrets are encrypted at rest (Data Protection, `v1:enc:`); `false` = keys stay memory-only (never touch disk) |
| `Settings:TokenHours` | `8` | Settings session lifetime |
| `Settings:MinPasswordLength` | `8` | Enforced on change |
| `Settings:ExhaustedCooldownSeconds` | `60` | 429 → short cooldown, then retry |
| `Settings:InvalidKeyCooldownHours` | `24` | 401/403 → key marked `invalid`, skipped by fallback |
| `Settings:BadConfigCooldownMinutes` | `10` | Other 4xx → config error, cooled down |

Runtime powers (all via `/api/ai/settings`, `X-Settings-Token`):

- **LLM API keys** — any vendor, multiple keys, priority order = fallback order;
  blanks/omitted key field on save = keep stored key; `/test` round-trips live
  and writes back health (`ok` / `invalid` / `cooling`).
- **Voice** — STT/TTS provider per side (`Browser` = free browser default,
  `Echo` = keyless demo, `OpenAiCompatible` = any vendor endpoint + key).
  Blank key on save = keep stored key.
- **Knowledge documents** — upload `.pdf` / `.docx` / `.md` / `.txt` (max 20 MB),
  parsed + chunked server-side; answers then cite the file.
- **Password** — panel change overrides `Settings:Password`
  (`passwordSource`: `config` → `stored`).

The Angular client is `AiSettingsService` (login/snapshot/keys/voice/kb);
`AiVoiceService` covers runtime `stt`/`tts`.

## Angular configuration (no secrets — ever)

```typescript
{ provide: AI_SUITE_CONFIG, useValue: { apiBaseUrl: 'https://api.myapp.com' } }
```

`apiBaseUrl` (your BFF backend) is the only URL the browser needs. Swap per
build via Angular `environment.ts`. Verify no leakage: DevTools → Network shows
only YOUR domain (`/api/ai/*`) — never `openai.com` / `microsofttranslator.com`.

### Script anomaly signal (CANDIDATE, additive)

The self-hosted engine checks each translated item for letters from a script that is neither the target language's own script, Latin, nor a script already present in the source item. The check never edits the output and says nothing about translation accuracy. Limits: a source that itself contains a foreign script allows that script in the output; languages without a known script are not checked; garbled text inside the correct script is not detected.

- Sidecar `/translate` adds `quality: {"checked": bool, "script_anomaly_items": [index]}`.
- API single route: `data.scriptAnomaly` (`null` = not checked, `true`/`false`). Batch route: `data.scriptAnomalyIndexes` (omitted/`null` = not checked).
- The JS SDK keeps the original text for a flagged item and sets `data-pixous-i18n="partial"`. Older servers do not send the fields, so SDK behaviour is unchanged for them.
- Translation routes return the translated text UNEDITED even when flagged; callers decide how to treat a flagged item. `null`/absent means not checked and must not be read as verified clean; malformed or missing sidecar metadata is treated as not checked.
- Chat replies translated with `responseLanguage`: when flagged, the translation is WITHHELD and `reply` is the model's original-language text with `replyScriptAnomaly: true`. The Angular widget then shows that text plus a short note. `false`/`null` leave the translated reply unchanged.
- Activation: if this candidate code is applied, the check is ALWAYS ON for the self-hosted engine (no switch) and the fields above appear in responses. Nothing is deployed anywhere by this work.
- Known false negatives: Latin is always allowed, so English garble in a Tamil output is not flagged; one foreign letter in the source allows that whole script in the output.

## Cancellation and shutdown semantics (CANDIDATE, scoped to what was tested)

- A caller abort cancels the .NET request token. The provider checks it before sending, and the retry back-off wait is cancelled too (unit tested). If the abort happens while the sidecar is decoding, the sidecar's worker is stopped and restarted (one restart observed) so the next request is not stuck behind the abandoned decode: measured once, next request 1.8 s after a browser abort through the sample API.
- A proxy that does NOT propagate the client abort keeps the sidecar decoding: in the same single-run control the next request took 53.8 s. Reverse proxies and custom gateways in front of the API must forward client disconnects.
- Waiting (queued) requests that are cancelled are dropped without disturbing the running one (one trace, 0.1 s resolution; not a fairness guarantee).
- Graceful shutdown: the sidecar waits up to `PIXOUS_SHUTDOWN_SECONDS` (default 10) for work. In one test with 1 running and 2 waiting requests it exited in 10.2 s, every client got the generic 500, no processes were left and a restart was healthy. A request still pending at shutdown gets a generic error (500 from the sidecar, a generic upstream failure from the API); this is not a clean-shutdown or zero-loss contract.
- These are single observed runs on one 2 GB host with the int8 engine; they are not rates.
