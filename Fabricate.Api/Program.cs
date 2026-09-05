using Fabricate.Api;
using Fabricate.Api.Authentication;
using Fabricate.Api.Routes;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Export;
using Fabricate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// #78 — validate in every environment, not just Development. Without this, a singleton depending on a scoped
// service silently captures it in Production: one FabricateDbContext would be shared by every request.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services
    .AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

// Enums travel as their names ("Anthropic", "ReviewRequired"), which is what the REST reference documents and
// what the TypeScript SDK sends. Without this they only bind numerically and those payloads fail with 400.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// #68 — the policies below are attached to every authenticated route group. A named policy that nothing calls
// RequireRateLimiting on is not enforced, which is what happened before: only the credential-validate endpoint
// was limited. Windows are partitioned per API key so one tenant cannot exhaust another's allowance.
var apiRateLimit = int.TryParse(Environment.GetEnvironmentVariable("FABRICATE_API_RATE_LIMIT_PER_MINUTE"), out var configuredLimit) && configuredLimit > 0
    ? configuredLimit
    : 100;

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "The API rate limit for this key has been exceeded. Retry after the window resets.",
        }, cancellationToken);
    };

    o.AddPolicy(RateLimitPolicies.Api, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPolicies.PartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = apiRateLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // Credential validation makes a real provider call; keep it from being usable as a key-testing oracle.
    o.AddPolicy(LlmCredentialRoutes.ValidateRateLimitPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPolicies.PartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 10,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddFabricateApplication(seed: 42);
builder.Services.AddFabricateInfrastructure(opts =>
{
    opts.Provider = builder.Configuration["SchemaProvider:Provider"] ?? "sqlite";
    opts.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=fabricate.db";
});

// LLM access: FABRICATE_LLM_* configures the operator's platform credential (optional); workspaces may bring their own.
var llmOptions = LlmOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddFabricateLlm(llmOptions, Environment.GetEnvironmentVariable("FABRICATE_DATA_PROTECTION_KEYS_PATH"));

// Durable persistence: FABRICATE_DB_PROVIDER=postgres (hosted deployments) or sqlite (local); unset keeps the
// in-memory repositories. Registered before the bootstrap service so migrations run before the seed.
var dbProvider = Environment.GetEnvironmentVariable("FABRICATE_DB_PROVIDER") ?? "memory";
if (!dbProvider.Equals("memory", StringComparison.OrdinalIgnoreCase))
{
    var connStr = Environment.GetEnvironmentVariable("FABRICATE_CONNECTION_STRING")
        ?? (dbProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
            ? "Data Source=fabricate.db"
            : throw new InvalidOperationException("FABRICATE_CONNECTION_STRING is required when FABRICATE_DB_PROVIDER is 'postgres'."));
    builder.Services.AddFabricatePersistence(dbProvider, connStr);
}

// Audit retention (#74): FABRICATE_AUDIT_RETENTION_DAYS defaults to 0, meaning events are kept indefinitely.
var auditRetention = AuditRetentionOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(auditRetention);

// Per-request usage auditing (#72): FABRICATE_API_USAGE_SAMPLING (0.0-1.0, default 1.0 = record every request).
builder.Services.AddSingleton(ApiUsageAuditOptions.FromEnvironment(Environment.GetEnvironmentVariable));

builder.Services.AddHostedService<StartupBootstrapService>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHostedService<ArtifactRetentionService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// After authentication and routing, so the middleware can see both the API key and the matched route template.
app.UseMiddleware<ApiUsageAuditMiddleware>();

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

// Health probe — no authentication required. Reports LLM configuration state without any value from it.
app.MapGet("/healthz", (LlmOptions llm) => Results.Ok(new
{
    status = "healthy",
    llm = new
    {
        platformCredential = llm.IsPlatformCredentialConfigured ? "configured" : "disabled",
        provider = llm.Provider,
        model = llm.Model,
        platformFallback = llm.PlatformFallback.ToString(),
    },
}))
   .AllowAnonymous()
   .WithName("Healthz")
   .WithTags("Health");

app.MapAccountRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapWorkspaceRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapProjectRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapRunRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapWorkflowRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapChatRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapLlmCredentialRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapApiKeyRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapWebhookRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapAuditRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapLlmUsageRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapSnapshotRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);
app.MapAccountLlmUsageRoutes().RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Api);

app.Run();

// Exposes the implicitly-generated Program class to WebApplicationFactory in the test project (#79).
public partial class Program;
