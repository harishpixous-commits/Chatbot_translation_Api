# Deployment

1. **Config per environment**: `appsettings.{Env}.json` overrides provider,
   model, languages, resilience/rate-limit; secrets ONLY via env vars or
   secret store (never files in prod):
   `AiSuite__ChatProvider`, `AiSuite__OpenAiCompatible__Endpoint`,
   `AiSuite__ApiKeys__Chat` (Key Vault reference), …
2. **Key Vault (Azure)** via `AddAzureKeyVault` + Managed Identity
   (`DefaultAzureCredential`); **AWS** via SystemsManager/SecretsManager
   configuration providers — both feed the same `ApiKeyConfigKey` pointers.
3. **Harden the host**: HTTPS only, exact-origin CORS, `RequireAuthorization`,
   rate limiting on, `/selection` behind admin auth, log shipping with secret
   redaction (services already avoid logging content/secrets).
4. **Angular**: `apiBaseUrl` per `environment.prod.ts`; serve `assets/i18n`.
5. **Health**: `GET /api/ai/info` doubles as a readiness probe (200 + expected
   `effectiveChatProvider`; `apiKeyConfigured: true` for keyed providers).

See [versioning](versioning.md) for the release/upgrade strategy.
