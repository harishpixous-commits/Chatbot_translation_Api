# Providers

## Selection precedence (every call)

```
per-request Provider field → runtime selection (POST /api/ai/selection) → configuration
```

## Chat providers

| Name | Backend | Key? | Use when |
|---|---|---|---|
| `Echo` | None (in-process) | Never | Dev, tests, offline fallback |
| `KnowledgeBase` | `AiSuite:KnowledgeBase` entries | Never | Instant on-brand FAQ answers, zero cost; front layer before real AI |
| `OpenAiCompatible` | Any `/chat/completions` HTTP endpoint | Via `ApiKeyConfigKey` (or `RequireApiKey: false`) | OpenAI, Azure OpenAI (`…/openai/v1`), Ollama, LM Studio, vLLM |

## Translation providers

| Name | Backend | Key? | Use when |
|---|---|---|---|
| `Echo` | None (tags `[lang]`) | Never | Dev, tests, pipeline verification |
| `AzureTranslator` | Azure AI Translator REST (3.0) | Subscription key via pointer | Production quality + detection |
| `LibreTranslate` | Self-hosted LibreTranslate | Usually none | Data-sovereignty / offline |

Only configured providers register: empty endpoint/region = adapter stays out of
the factory, and selecting it returns 503 `PROVIDER_NOT_CONFIGURED` (never a crash).

## Adding a provider (no core changes)

1. Implement the seam (~30 lines): `IChatProvider` (`ProviderName` +
   `StreamChatAsync`; `ChatAsync` folds free) or `ITranslationProvider`
   (3 methods). Wrap HTTP with `ProviderResilience.SendAsync` for free
   timeout/retry. For Semantic Kernel: inject your `Kernel`, call
   `IChatCompletionService` inside `StreamChatAsync`.
2. Register in the consuming project: `services.AddSingleton<IChatProvider, MyProvider>();`
3. Select: `"AiSuite": { "ChatProvider": "MyProvider" }` (or POST `/selection`).
4. Add tests with a fake (never real keys).

## Optional NuGet (consumer-side, never required by the base)

`Microsoft.Extensions.AI.Abstractions` + `Microsoft.Extensions.AI.OpenAI`
(10.x, wrap any `IChatClient`); `Microsoft.SemanticKernel` (+Connectors.OpenAI)
1.80.1 (plugins/function-calling); `Azure.AI.Translation.Text` 2.0.0 (typed
Translator SDK); `Google.Cloud.Translate.V3` 3.x; `Azure.Identity` 1.x
(Managed Identity > keys); `Azure.Extensions.AspNetCore.Configuration.Secrets`
(Key Vault); `Microsoft.AspNetCore.Mvc.Testing` (integration tests).
