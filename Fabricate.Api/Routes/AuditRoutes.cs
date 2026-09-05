using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

public static class AuditRoutes
{
    public static RouteGroupBuilder MapAuditRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts/{accountId:guid}/audit").WithTags("Audit");

        group.MapGet("/", async (
            Guid accountId,
            IAuditLogService auditLog,
            IAccountService accounts,
            HttpContext ctx,
            CancellationToken ct,
            int page = 1,
            int pageSize = 50,
            string? action = null,
            string? actionPrefix = null,
            Guid? apiKeyId = null) =>
        {
            await accounts.EnsureMemberAsync(accountId, ctx.GetUserId(), ct).ConfigureAwait(false);

            // actionPrefix and apiKeyId exist because per-request usage now shares this log with security
            // events (#72): without them, one busy key drowns everything else on the first page.
            var filter = new AuditFilter(action, actionPrefix, apiKeyId);
            var result = await auditLog.QueryAsync(accountId, filter, page, pageSize, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).WithName("QueryAuditLog");

        group.MapGet("/export", async (
            Guid accountId,
            IAuditLogService auditLog,
            HttpContext ctx,
            CancellationToken ct,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string format = "json") =>
        {
            var isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
            if (!isCsv && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem($"Unsupported export format '{format}'. Use 'json' or 'csv'.", statusCode: StatusCodes.Status400BadRequest);
            }

            // The authorisation check lives behind the first MoveNext, so it is forced here: once the response
            // has started streaming, a 403 can no longer be sent.
            var events = auditLog.ExportAsync(accountId, ctx.GetUserId(), from, to, ct);
            await using var enumerator = events.GetAsyncEnumerator(ct);
            bool hasFirst;
            try
            {
                hasFirst = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }

            var fileName = $"audit-{accountId}-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}.{(isCsv ? "csv" : "json")}";
            ctx.Response.ContentType = isCsv ? "text/csv; charset=utf-8" : "application/json; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

            // Written through the response PipeWriter rather than its Stream: Utf8JsonWriter and StreamWriter
            // both flush synchronously when their buffer fills, and the server disallows synchronous IO.
            var output = ctx.Response.BodyWriter;
            await (isCsv
                ? WriteCsvAsync(output, enumerator, hasFirst, ct)
                : WriteJsonAsync(output, enumerator, hasFirst, ct)).ConfigureAwait(false);

            return Results.Empty;
        }).WithName("ExportAuditLog");

        return group;
    }

    private static async Task WriteJsonAsync(
        PipeWriter output,
        IAsyncEnumerator<AuditEvent> events,
        bool hasFirst,
        CancellationToken cancellationToken)
    {
        await using var writer = new Utf8JsonWriter(output);
        writer.WriteStartArray();

        for (var more = hasFirst; more; more = await events.MoveNextAsync().ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, events.Current, AuditExportJson.Options);

            // Flush per event so a large export streams instead of buffering the whole array.
            writer.Flush();
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        writer.Flush();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCsvAsync(
        PipeWriter output,
        IAsyncEnumerator<AuditEvent> events,
        bool hasFirst,
        CancellationToken cancellationToken)
    {
        // "\n" explicitly, not Environment.NewLine: the file is data, and its shape must not depend on which
        // operating system produced it.
        Write(output, "id,accountId,actorUserId,apiKeyId,action,targetType,targetId,correlationId,occurredAt,details\n");

        for (var more = hasFirst; more; more = await events.MoveNextAsync().ConfigureAwait(false))
        {
            var e = events.Current;
            Write(output, string.Join(',', new[]
            {
                Csv(e.Id.ToString()),
                Csv(e.AccountId.ToString()),
                Csv(e.ActorUserId?.ToString()),
                Csv(e.ApiKeyId?.ToString()),
                Csv(e.Action),
                Csv(e.TargetType),
                Csv(e.TargetId),
                Csv(e.CorrelationId),
                Csv(e.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(e.Details),
            }) + "\n");

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Write(IBufferWriter<byte> output, string text)
        => Encoding.UTF8.GetBytes(text, output);

    /// <summary>RFC 4180: quote every field, doubling any quote inside it.</summary>
    private static string Csv(string? value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

internal static class AuditExportJson
{
    /// <summary>camelCase, matching every other response body the API produces.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
