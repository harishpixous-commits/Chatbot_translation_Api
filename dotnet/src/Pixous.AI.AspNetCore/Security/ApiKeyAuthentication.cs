using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pixous.AI.AspNetCore.Security;

/// <summary>Options for the built-in <c>PixousApiKey</c> scheme.</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Header carrying the key. Default: X-API-Key.</summary>
    public string HeaderName { get; set; } = "X-API-Key";

    /// <summary>Accepted keys (from AiSuite:Security:ApiKeys). Compared in constant time.</summary>
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Minimal self-hosted API-key scheme so a module host can flip
/// AiSuite:Security:RequireAuthorization on WITHOUT standing up an IdP.
/// Keys come from AiSuite:Security:ApiKeys (User Secrets / env vars / Key Vault),
/// are compared in constant time, and are never logged or echoed. Missing and
/// wrong keys take the identical NoResult path (no oracle between them); the
/// challenge is the framework's fixed 401 with an empty body. Hosts with
/// JWT/OIDC register their own scheme instead — RequireAuthorization accepts either.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string SchemeName = "PixousApiKey";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.HeaderName, out var presented)
            || presented.Count != 1
            || string.IsNullOrEmpty(presented[0]))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presentedBytes = Encoding.UTF8.GetBytes(presented[0]!);
        foreach (var key in Options.Keys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length == presentedBytes.Length
                && CryptographicOperations.FixedTimeEquals(keyBytes, presentedBytes))
            {
                var identity = new ClaimsIdentity(SchemeName);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "api-key-caller"));
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
