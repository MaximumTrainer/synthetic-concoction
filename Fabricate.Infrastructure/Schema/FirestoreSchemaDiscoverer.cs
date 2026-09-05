using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Google.Cloud.Firestore;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// GCP Firestore schema discovery adapter.
/// Lists root collections for the given project, samples up to 200 documents per
/// collection to infer field types from Firestore value kinds.
/// </summary>
/// <remarks>
/// Authentication: Application Default Credentials (ADC) resolved via
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c> or Workload Identity on GKE.
///
/// The <c>connectionString</c> parameter is used as the GCP project ID.
/// Alternatively set the <c>GOOGLE_CLOUD_PROJECT</c> environment variable.
/// Named Firestore databases can be specified via <c>databaseName</c>; leave empty
/// for the default <c>(default)</c> database.
/// </remarks>
public sealed class FirestoreSchemaDiscoverer : INoSqlSchemaDiscoverer
{
    public string ProviderName => "firestore";

    public async Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        string projectId = FirestoreConnection.ResolveProjectId(connectionString);

        FirestoreDb db = await FirestoreConnection.CreateAsync(projectId, databaseName, cancellationToken);

        var results = new List<CollectionMetadata>();
        await foreach (CollectionReference collRef in db.ListRootCollectionsAsync().WithCancellation(cancellationToken))
        {
            QuerySnapshot snapshot = await collRef.Limit(200).GetSnapshotAsync(cancellationToken);

            var acc = new FieldInferenceHelper.FieldAccumulator();
            foreach (DocumentSnapshot doc in snapshot.Documents)
                InferFieldsFromFirestore(acc, doc.ToDictionary());

            string dbLabel = string.IsNullOrWhiteSpace(databaseName) ? "(default)" : databaseName;
            results.Add(new CollectionMetadata(
                $"{projectId}/{dbLabel}",
                collRef.Id,
                acc.Build(),
                null,
                Array.Empty<CollectionIndexDescriptor>()));
        }

        return results;
    }

    private static void InferFieldsFromFirestore(FieldInferenceHelper.FieldAccumulator acc, IDictionary<string, object> fields)
    {
        foreach (var (name, value) in fields)
        {
            bool isNull = value is null;
            DocumentFieldType type;

            if (isNull)
            {
                type = DocumentFieldType.Null;
            }
            else
            {
                type = value switch
                {
                    string => DocumentFieldType.String,
                    bool => DocumentFieldType.Boolean,
                    long or int => DocumentFieldType.Number,
                    double or float => DocumentFieldType.Number,
                    Timestamp => DocumentFieldType.Date,
                    DateTime or DateTimeOffset => DocumentFieldType.Date,
                    IDictionary<string, object> => DocumentFieldType.Object,
                    System.Collections.IList => DocumentFieldType.Array,
                    DocumentReference => DocumentFieldType.String,
                    GeoPoint => DocumentFieldType.String,
                    _ when value!.GetType().Name == "ByteString" => DocumentFieldType.Binary,
                    _ => DocumentFieldType.Unknown
                };
            }

            if (type == DocumentFieldType.Object && value is IDictionary<string, object> nested)
                acc.Observe(name, type, isNull, n => InferFieldsFromFirestore(n, nested));
            else
                acc.Observe(name, type, isNull);
        }
    }
}
