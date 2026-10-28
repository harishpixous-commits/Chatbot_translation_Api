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
| `OpenAiCompatible` | Any `/chat/completions` HTTP endpoint | Via `ApiKeyConfigKey` (or `RequireApiKey: false`) | OpenAI, Azure OpenAI (`…/openai/v1`), Ollama, LM Studio, vLLM, llama.cpp |
| `MultiLLM` | Any mix of vendor presets (below) | Per-key, saved in the ⚙ Settings panel | Hosted vendors with operator-managed keys + fallback chain |

## Two chat modes (both ship in the box)

**1. Local, self-hosted model (default posture — no external AI calls).**
Point `OpenAiCompatible` at a local server: the bundled llama.cpp chat sidecar
(`chat-sidecar/`, pinned Qwen3-4B GGUF) or Ollama/LM Studio/vLLM. Set
`OpenAiCompatible:RequireApiKey` to `false`; no key anywhere.

**2. Hosted vendor with an API key (opt-in configuration).**
Same code path, key supplied by the operator — never in source, never sent to
the browser:
- Single endpoint: `OpenAiCompatible:Endpoint` + `Model`, key referenced by
  `OpenAiCompatible:ApiKeyConfigKey` (User Secrets / env / Key Vault).
- Multi-vendor with fallback: `ChatProvider: "MultiLLM"`; keys saved in the
  password-gated ⚙ Settings panel against a vendor preset — OpenAI, Anthropic,
  Gemini, DeepSeek, Groq, Mistral, OpenRouter, Ollama (local), or Custom
  OpenAI-compatible. API responses mask keys; a 401/403/429 cools that key and
  tries the next; ambiguous failures (timeout/5xx) do NOT silently fail over
  (possible duplicate billing) unless the operator opts in.

Switching between the two modes is configuration only — no code change, and
the default stays the local model unless an operator selects otherwise.

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
