using System.Security.Claims;
using System.Text.Encodings.Web;
using Fabricate.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Fabricate.Api.Authentication;

public sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions { }

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeyService)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValues))
            return AuthenticateResult.Fail("Missing X-Api-Key header.");

        var plaintextKey = headerValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(plaintextKey))
            return AuthenticateResult.Fail("Empty X-Api-Key header.");

        var apiKey = await apiKeyService.ValidateAsync(plaintextKey, Context.RequestAborted)
            .ConfigureAwait(false);

        if (apiKey is null || !apiKey.IsActive)
            return AuthenticateResult.Fail("Invalid or expired API key.");

        var claims = new List<Claim>
        {
            new("sub", apiKey.AccountId.ToString()),
            new("uid", apiKey.AccountId.ToString()),
            new("account_id", apiKey.AccountId.ToString()),
            new("api_key_id", apiKey.Id.ToString()),
        };

        foreach (var scope in apiKey.Scopes)
            claims.Add(new Claim("scope", scope));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
