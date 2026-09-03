using Fabricate.Api;
using Fabricate.Api.Authentication;
using Fabricate.Api.Routes;
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

builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("api", opt =>
{
    opt.Window = TimeSpan.FromMinutes(1);
    opt.PermitLimit = 100;
    opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    opt.QueueLimit = 0;
}));

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

builder.Services.AddHostedService<StartupBootstrapService>();

var dbProvider = Environment.GetEnvironmentVariable("FABRICATE_DB_PROVIDER") ?? "memory";
if (dbProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
{
    var connStr = Environment.GetEnvironmentVariable("FABRICATE_CONNECTION_STRING")
        ?? "Data Source=fabricate.db";
    builder.Services.AddFabricatePersistence(connStr);
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

// Health probe — no authentication required.
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
   .AllowAnonymous()
   .WithName("Healthz")
   .WithTags("Health");

app.MapAccountRoutes().RequireAuthorization();
app.MapWorkspaceRoutes().RequireAuthorization();
app.MapProjectRoutes().RequireAuthorization();
app.MapRunRoutes().RequireAuthorization();
app.MapWorkflowRoutes().RequireAuthorization();
app.MapChatRoutes().RequireAuthorization();
app.MapApiKeyRoutes().RequireAuthorization();
app.MapWebhookRoutes().RequireAuthorization();

app.Run();
