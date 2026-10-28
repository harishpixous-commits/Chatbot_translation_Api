using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;

namespace Pixous.AI.Providers;

/// <summary>
/// Builds the right chat adapter for a saved key entry. Any vendor works:
/// Anthropic and Gemini get native wire formats, everything else (OpenAI,
/// DeepSeek, Groq, Mistral, OpenRouter, Ollama, LM Studio, vLLM, Custom…) is
/// OpenAI-compatible. Endpoint/model/key always come from the entry itself.
/// </summary>
public static class LlmVendor
{
    public static IChatProvider Create(HttpClient http, LlmKeyEntry key, ResilienceOptions resilience) =>
        key.Vendor.Trim().ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicChatProvider(http, key, resilience),
            "gemini" or "google" or "google gemini" => new GeminiChatProvider(http, key, resilience),
            _ => new OpenAiCompatibleChatProvider(
                http,
                new OpenAiCompatibleOptions
                {
                    Endpoint = key.Endpoint,
                    Model = key.Model,
                    MaxTokens = key.MaxTokens ?? 512,
                    Temperature = key.Temperature ?? 0.7,
                    ApiKeyConfigKey = "(settings store)",
                    RequireApiKey = false // key is supplied directly from the entry
                },
                resilience,
                key.ApiKey)
        };
}
