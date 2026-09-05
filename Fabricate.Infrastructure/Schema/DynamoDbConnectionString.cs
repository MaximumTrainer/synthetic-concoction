using Amazon;
using Amazon.DynamoDBv2;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Builds a DynamoDB client from the connection-string format both DynamoDB adapters accept.
///
/// <para>
/// Shared because the CLI hands the same <c>--connection</c> to <c>discover</c> and
/// <c>discover-profile</c>. The profiler originally treated the whole string as a service URL while the
/// discoverer parsed <c>region=...;serviceUrl=...</c>, so the same argument worked for one command and failed for
/// the other (#91).
/// </para>
/// </summary>
internal static class DynamoDbConnectionString
{
    /// <summary>
    /// Parses <c>region=us-east-1;serviceUrl=http://localhost:8000</c>. Either key may be omitted: the region
    /// falls back to <c>AWS_DEFAULT_REGION</c> and then <c>us-east-1</c>, and no service URL means the real AWS
    /// endpoint for that region. Credentials always come from the standard chain — an IAM role rather than a
    /// stored key.
    /// </summary>
    internal static AmazonDynamoDBClient CreateClient(string? connectionString)
    {
        string? region = null;
        string? serviceUrl = null;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2) continue;

                switch (pair[0].Trim().ToLowerInvariant())
                {
                    case "region": region = pair[1].Trim(); break;
                    case "serviceurl": serviceUrl = pair[1].Trim(); break;
                }
            }
        }

        region ??= Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1";

        var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
        if (!string.IsNullOrEmpty(serviceUrl)) config.ServiceURL = serviceUrl;

        return new AmazonDynamoDBClient(config);
    }
}
