using Fabricate.Application.Abstractions;
using Fabricate.Application.Compliance;
using Fabricate.Application.Constraints;
using Fabricate.Application.Generation;
using Fabricate.Application.Orchestration;
using Fabricate.Application.Planning;
using Fabricate.Application.Schema;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Tests.Integration;

/// <summary>
/// A schema exercising the cases that break exporters and planners — a self-reference, a two-table cycle, a
/// composite unique constraint, nullable FKs and a spread of DataKinds — plus a fully wired orchestrator.
/// Shared by the determinism (#80), round-trip (#81) and memory-budget (#82) suites.
/// </summary>
internal static class GenerationFixture
{
    public static DatabaseSchema Schema { get; } = new("fixture",
    [
        new TableSchema("main", "users",
        [
            new ColumnSchema("id", "INTEGER", DataKind.Integer, false, true, true, null, null, null, null),
            new ColumnSchema("email", "TEXT", DataKind.Email, false, false, true, 200, null, null, null),
            new ColumnSchema("display_name", "TEXT", DataKind.String, false, false, false, 60, null, null, null),
            new ColumnSchema("created_at", "TEXT", DataKind.DateTime, false, false, false, null, null, null, null),
            new ColumnSchema("is_active", "INTEGER", DataKind.Boolean, false, false, false, null, null, null, null),
            new ColumnSchema("balance", "REAL", DataKind.Decimal, true, false, false, null, 12, 2, null),
            // Self-reference: every row but the first points at its predecessor.
            new ColumnSchema("manager_id", "INTEGER", DataKind.Integer, true, false, false, null, null, null, null),
        ],
        ["id"],
        [new ForeignKeySchema("fk_users_manager", "main.users", ["manager_id"], "main.users", ["id"])],
        [new UniqueConstraintSchema("uq_users_email", ["email"])],
        []),

        new TableSchema("main", "orders",
        [
            new ColumnSchema("id", "INTEGER", DataKind.Integer, false, true, true, null, null, null, null),
            new ColumnSchema("user_id", "INTEGER", DataKind.Integer, false, false, false, null, null, null, null),
            new ColumnSchema("reference", "TEXT", DataKind.Guid, false, false, true, null, null, null, null),
            new ColumnSchema("placed_on", "TEXT", DataKind.Date, false, false, false, null, null, null, null),
            new ColumnSchema("total", "REAL", DataKind.Decimal, false, false, false, null, 12, 2, null),
            new ColumnSchema("notes", "TEXT", DataKind.String, true, false, false, 200, null, null, null),
        ],
        ["id"],
        [new ForeignKeySchema("fk_orders_users", "main.orders", ["user_id"], "main.users", ["id"])],
        [new UniqueConstraintSchema("uq_orders_reference", ["reference"])],
        []),

        new TableSchema("main", "order_items",
        [
            new ColumnSchema("id", "INTEGER", DataKind.Integer, false, true, true, null, null, null, null),
            new ColumnSchema("order_id", "INTEGER", DataKind.Integer, false, false, false, null, null, null, null),
            new ColumnSchema("sku", "TEXT", DataKind.String, false, false, false, 32, null, null, null),
            new ColumnSchema("quantity", "INTEGER", DataKind.Integer, false, false, false, null, null, null, null),
            new ColumnSchema("unit_price", "REAL", DataKind.Decimal, false, false, false, null, 12, 2, null),
        ],
        ["id"],
        [new ForeignKeySchema("fk_items_orders", "main.order_items", ["order_id"], "main.orders", ["id"])],
        [new UniqueConstraintSchema("uq_item_order_sku", ["order_id", "sku"])],
        []),
    ]);

    /// <summary>DDL matching <see cref="Schema"/>, for the round-trip tests that apply generated SQL.</summary>
    public static string SqliteDdl => """
        CREATE TABLE users (
          id INTEGER PRIMARY KEY,
          email TEXT NOT NULL UNIQUE,
          display_name TEXT NOT NULL,
          created_at TEXT NOT NULL,
          is_active INTEGER NOT NULL,
          balance REAL NULL,
          manager_id INTEGER NULL REFERENCES users(id)
        );
        CREATE TABLE orders (
          id INTEGER PRIMARY KEY,
          user_id INTEGER NOT NULL REFERENCES users(id),
          reference TEXT NOT NULL UNIQUE,
          placed_on TEXT NOT NULL,
          total REAL NOT NULL,
          notes TEXT NULL
        );
        CREATE TABLE order_items (
          id INTEGER PRIMARY KEY,
          order_id INTEGER NOT NULL REFERENCES orders(id),
          sku TEXT NOT NULL,
          quantity INTEGER NOT NULL,
          unit_price REAL NOT NULL,
          UNIQUE (order_id, sku)
        );
        """;

    public static string PostgresDdl => """
        CREATE SCHEMA IF NOT EXISTS main;
        SET search_path TO main;
        CREATE TABLE main.users (
          id BIGINT PRIMARY KEY,
          email TEXT NOT NULL UNIQUE,
          display_name TEXT NOT NULL,
          created_at TEXT NOT NULL,
          is_active BOOLEAN NOT NULL,
          balance DOUBLE PRECISION NULL,
          manager_id BIGINT NULL REFERENCES main.users(id)
        );
        CREATE TABLE main.orders (
          id BIGINT PRIMARY KEY,
          user_id BIGINT NOT NULL REFERENCES main.users(id),
          reference TEXT NOT NULL UNIQUE,
          placed_on TEXT NOT NULL,
          total DOUBLE PRECISION NOT NULL,
          notes TEXT NULL
        );
        CREATE TABLE main.order_items (
          id BIGINT PRIMARY KEY,
          order_id BIGINT NOT NULL REFERENCES main.orders(id),
          sku TEXT NOT NULL,
          quantity BIGINT NOT NULL,
          unit_price DOUBLE PRECISION NOT NULL,
          UNIQUE (order_id, sku)
        );
        """;

    /// <summary>The insert order the exported SQL must be applied in for foreign keys to hold.</summary>
    public static IReadOnlyList<string> TablesInDependencyOrder { get; } = ["main.users", "main.orders", "main.order_items"];

    public static ISyntheticDataOrchestrator CreateOrchestrator(long seed)
    {
        var random = new DeterministicRandomService(seed);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);

        return new SyntheticDataOrchestrator(
            new SchemaDiscoveryService(new FixtureSchemaProvider(Schema)),
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());
    }

    public static GenerationRequest CreateRequest(long seed, int rowsPerTable = 20, ComplianceProfile profile = ComplianceProfile.Default)
        => new(Schema,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["main.users"] = rowsPerTable,
                ["main.orders"] = rowsPerTable * 2,
                ["main.order_items"] = rowsPerTable * 3,
            },
            seed,
            Rules: null,
            ComplianceProfile: profile);

    private sealed class FixtureSchemaProvider(DatabaseSchema schema) : ISchemaProvider
    {
        public string ProviderName => "fixture";
        public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(schema);
    }
}
