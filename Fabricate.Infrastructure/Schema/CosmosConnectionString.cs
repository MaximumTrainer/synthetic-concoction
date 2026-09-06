using System.Data.Common;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Builds a Cosmos DB client from a connection string, shared by the Cosmos discoverer and profiler so the same
/// <c>--connection</c> means the same thing to <c>discover</c> and <c>discover-profile</c> (#91).
/// </summary>
/// <remarks>
/// Beyond the account endpoint and key that the SDK parses itself, two optional keys are recognised and stripped
/// before the string reaches the SDK:
///
/// <list type="bullet">
///   <item><c>ConnectionMode=Gateway</c> — the SDK defaults to direct mode, which needs a range of TCP ports open.
///   Gateway mode talks HTTPS to one endpoint, which is what works from behind a corporate proxy or a restrictive
///   egress policy, and is also the only mode the Cosmos emulator serves.</item>
///   <item><c>DisableServerCertificateValidation=True</c> — for the emulator, which presents a self-signed
///   certificate. It is refused when the endpoint is not a loopback address, so it cannot be used to weaken a
///   connection to a real account by accident.</item>
/// </list>
/// </remarks>
internal static class CosmosConnectionString
{
    internal static CosmosClient CreateClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        // Both Cosmos adapters read documents as System.Text.Json JsonElement. The SDK's default serializer is
        // Newtonsoft-based and cannot populate one: it yields JsonValueKind.Undefined rather than failing, so the
        // discoverer inferred no fields and the profiler produced a collection with the right document count and
        // no field profiles at all. Nothing caught it because neither adapter had ever read a document (#91).
        var options = new CosmosClientOptions
        {
            ApplicationName = "Fabricate",
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };

        if (Take(builder, "ConnectionMode") is { } mode && mode.Equals("gateway", StringComparison.OrdinalIgnoreCase))
        {
            options.ConnectionMode = ConnectionMode.Gateway;
        }

        if (Take(builder, "DisableServerCertificateValidation") is { } disable && bool.TryParse(disable, out var yes) && yes)
        {
            var endpoint = builder.TryGetValue("AccountEndpoint", out var raw) ? raw as string : null;
            if (!IsLoopback(endpoint))
            {
                throw new InvalidOperationException(
                    "DisableServerCertificateValidation is only accepted for a loopback endpoint (the Cosmos emulator). " +
                    "A real account must present a valid certificate.");
            }

            options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
        }

        return new CosmosClient(builder.ConnectionString, options);
    }

    private static string? Take(DbConnectionStringBuilder builder, string key)
    {
        if (!builder.TryGetValue(key, out var value)) return null;
        builder.Remove(key);
        return value as string;
    }

    private static bool IsLoopback(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
