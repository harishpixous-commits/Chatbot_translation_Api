# Integration (end-to-end checklist)

Backend ([dotnet-integration](dotnet-integration.md)) + frontend
([angular-integration](angular-integration.md)) + verification:

1. Backend runs: `GET /api/ai/info` → `success: true`, expected providers.
2. Chat works: `POST /api/ai/chat {"message":"hi"}` → `data.reply`.
3. Translation works: `POST /api/ai/translation/translate` → `data.translatedText`.
4. Frontend points at the backend (`AI_SUITE_CONFIG.apiBaseUrl` per
   `environment.ts`); widget sends, switcher switches, ⚙ panel lists services.
5. Production hardening ([security](security.md)): config-only since 1.2.0 —
   auth on (built-in API-key scheme or host JWT/OIDC; startup fails closed
   without one), rate limit on by default, exact-origin CORS; HTTPS + Key
   Vault/env secrets remain host duties — no keys in the browser.
6. Sample to click through: run `samples/SampleApi`, open `/demo.html`
   (popup widget + ⚙ panel + streaming + voice + i18n badge).
