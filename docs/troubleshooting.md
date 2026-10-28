# Troubleshooting

| Symptom | Cause → Fix |
|---|---|
| `Invalid AiSuite configuration` at startup | `Validate()` failed — message names the key; compare with `samples/SampleApi/appsettings.json` |
| 503 `PROVIDER_NOT_CONFIGURED` | Selected provider not registered: endpoint/region empty (conditional registration) or key missing (message says which config key to set) |
| 502 `*_UNAVAILABLE` | Upstream down/invalid key/host unreachable; check `traceId` in server logs (provider + duration logged, content never) |
| 400 `VALIDATION_ERROR` | Empty/oversize input or language outside `Languages:Supported` |
| 400 `UNKNOWN_PROVIDER` | `/selection` name typo — check `GET /api/ai/providers` |
| `Sorry — unavailable` in widget | Backend not running / wrong `apiBaseUrl` / CORS — check Network tab (calls must hit YOUR domain) |
| `[xx] mock tag` in replies | Echo translation path doing its job — configure a real translation provider |
| Build file-lock errors (`MSB3021`) | A previous `dotnet run` still holds the port/DLL — stop it before rebuilding |
| Angular `TS2307` | `npm install` in `angular/pixous-ai-suite` first; keep `@angular/*` minor versions aligned |
| Languages list duplicated | Fixed: binder-append dedupe in `AddAiSuite` (keep the shipped version) |

Debug with `GET /api/ai/info` (effective providers, key flags) and
`GET /api/ai/selection` (available names) before touching code.
