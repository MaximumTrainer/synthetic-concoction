using Fabricate.Api;
using Fabricate.Api.Authentication;
using Fabricate.Api.Routes;
using Fabricate.Application.Llm;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("api", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });

    // Credential validation makes a real provider call; keep it from being usable as a key-testing oracle.
    o.AddFixedWindowLimiter(LlmCredentialRoutes.ValidateRateLimitPolicy, opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10;
        opt.QueueLimit = 0;
    });
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

builder.Services.AddHostedService<StartupBootstrapService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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

app.MapAccountRoutes().RequireAuthorization();
app.MapWorkspaceRoutes().RequireAuthorization();
app.MapProjectRoutes().RequireAuthorization();
app.MapRunRoutes().RequireAuthorization();
app.MapWorkflowRoutes().RequireAuthorization();
app.MapChatRoutes().RequireAuthorization();
app.MapLlmCredentialRoutes().RequireAuthorization();
app.MapApiKeyRoutes().RequireAuthorization();
app.MapWebhookRoutes().RequireAuthorization();

app.Run();
