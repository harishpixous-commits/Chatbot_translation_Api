using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

public class ProviderErrorSanitizerTests
{
    [Fact]
    public void Extracts_vendor_message_and_redacts_echoed_key()
    {
        var raw = "Chat backend returned 401. { \"error\": { \"message\": \"Incorrect API key provided: sk-fixtuREALLYLONGKEY0001. You can find your API key at https://platform.openai.com/account/api-keys.\" } }";
        var clean = ProviderErrorSanitizer.Sanitize(401, raw);
        Assert.Contains("401", clean);
        Assert.Contains("Incorrect API key provided", clean);
        Assert.DoesNotContain("sk-fixtuREALLYLONGKEY0001", clean);
        Assert.Contains("[redacted]", clean);
        Assert.DoesNotContain("{", clean);
        Assert.DoesNotContain("\"", clean);
    }

    [Fact]
    public void Redacts_common_key_shapes_even_without_json()
    {
        foreach (var leak in new[]
        {
            "failure with sk-abcDEF123456 in body",
            "api_key=AIzaSyD4iE2xVSpokenHere12345",
            "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9",
            "token gsk_abc123def456ghi789",
        })
        {
            var clean = ProviderErrorSanitizer.Sanitize(403, leak);
            Assert.Contains("[redacted]", clean);
            Assert.StartsWith("HTTP 403.", clean);
        }
    }

    [Fact]
    public void Plain_message_is_kept_concise_and_single_line()
    {
        var clean = ProviderErrorSanitizer.Sanitize(null, "  connection\n refused\tby host  ");
        Assert.Equal("connection refused by host", clean);
    }

    [Fact]
    public void Null_or_empty_becomes_generic()
    {
        Assert.Equal("HTTP 500. Provider call failed.", ProviderErrorSanitizer.Sanitize(500, null));
        Assert.Equal("HTTP 500. Provider call failed.", ProviderErrorSanitizer.Sanitize(500, "   "));
    }

    [Fact]
    public void Output_is_capped()
    {
        var clean = ProviderErrorSanitizer.Sanitize(500, new string('x', 500));
        Assert.True(clean.Length <= 160);
    }

    [Fact]
    public void Unparseable_json_falls_back_to_redacted_text()
    {
        var clean = ProviderErrorSanitizer.Sanitize(502, "bad gateway { not json sk-leakleakleak123");
        Assert.DoesNotContain("sk-leakleakleak123", clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void ForClient_redacts_credential_sentinels_and_urls()
    {
        // Adversarial provider body: echoes a key-shaped token, a bearer
        // header, an internal URL, and request-private fields. NONE of it may
        // reach the client-facing text.
        var raw = "{\"error\":{\"message\":\"bad key sk-liveSENTINELkey9999 see http://169.254.10.9:9000/internal/admin\"," +
                  "\"request\":\"harish@example.com secret prompt contents\"," +
                  "\"auth\":\"Bearer sentinel-bearer-token-12345\"}}";
        var clean = ProviderErrorSanitizer.SanitizeForClient(raw);
        Assert.DoesNotContain("sk-liveSENTINELkey9999", clean);
        Assert.DoesNotContain("169.254.10.9", clean);
        Assert.DoesNotContain("sentinel-bearer-token-12345", clean);
        Assert.DoesNotContain("harish@example.com", clean); // dropped with the non-message fields
        Assert.DoesNotContain("secret prompt contents", clean);
        Assert.Contains("[redacted]", clean);
        Assert.True(clean.Length <= 160);
    }

    [Fact]
    public void ForClient_redacts_urls_and_credentials_in_plain_text()
    {
        var clean = ProviderErrorSanitizer.SanitizeForClient(
            "upstream http://internal-host:8080/v1 rejected api_key=AIzaSyD4iE2xVSpokenHere12345");
        Assert.DoesNotContain("internal-host", clean);
        Assert.DoesNotContain("AIzaSyD4iE2xVSpokenHere12345", clean);
        Assert.Contains("[redacted-url]", clean);
    }

    [Fact]
    public void ForClient_keeps_plain_vendor_message_readable()
    {
        var clean = ProviderErrorSanitizer.SanitizeForClient("model overloaded, try again later");
        Assert.Equal("model overloaded, try again later", clean);
    }
}
