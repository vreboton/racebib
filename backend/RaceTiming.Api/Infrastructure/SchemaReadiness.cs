using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Infrastructure;

/// <summary>Conservative compatibility contract: exact migration history and relational structure.
/// Does not repair a partially applied MySQL DDL operation. Verification is read-only.</summary>
public static class SchemaReadiness
{
    public sealed record ReadyResult(string Status, string SchemaHash);
    private sealed record Column(string Type, bool Nullable);
    private sealed record Index(bool Unique, string[] Columns);
    private sealed record ForeignKey(string Table, string[] Columns, string[] PrincipalColumns, string Delete);
    private sealed class Table
    {
        public SortedDictionary<string, Column> Columns { get; } = new(StringComparer.Ordinal);
        public string[] PrimaryKey { get; set; } = [];
        public SortedDictionary<string, Index> Indexes { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, ForeignKey> ForeignKeys { get; } = new(StringComparer.Ordinal);
    }

    public static void RequireHistory(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var migrations = expected.ToArray();
        if (migrations.Length == 0 || !migrations.SequenceEqual(actual))
            throw new InvalidOperationException("Database migration history is missing or incompatible. Explicit recovery is required.");
    }

    public static void RequireContract(string expected, string actual, string? diagnostic = null)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException("Database structure does not match the application contract. Explicit recovery is required. " + diagnostic);
    }

    public static async Task<ReadyResult> VerifyAsync(RaceTimingDbContext db, CancellationToken ct = default)
    {
        RequireHistory(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(ct));
        var expected = new SortedDictionary<string, Table>(StringComparer.Ordinal);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            var contract = new Table();
            foreach (var column in table.Columns)
                contract.Columns.Add(column.Name, new Column(column.StoreType.ToLowerInvariant(), column.IsNullable));
            contract.PrimaryKey = table.PrimaryKey?.Columns.Select(x => x.Name).ToArray() ?? [];
            foreach (var index in table.Indexes)
                contract.Indexes.Add(index.Name, new Index(index.IsUnique, index.Columns.Select(x => x.Name).ToArray()));
            foreach (var key in table.UniqueConstraints.Where(x => x != table.PrimaryKey))
                contract.Indexes.Add(key.Name, new Index(true, key.Columns.Select(x => x.Name).ToArray()));
            foreach (var fk in table.ForeignKeyConstraints)
                contract.ForeignKeys.Add(fk.Name, new ForeignKey(fk.PrincipalTable.Name,
                    fk.Columns.Select(x => x.Name).ToArray(), fk.PrincipalColumns.Select(x => x.Name).ToArray(),
                    NormalizeDelete(fk.OnDeleteAction.ToString())));
            expected.Add(table.Name, contract);
        }

        var actual = new SortedDictionary<string, Table>(StringComparer.Ordinal);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // Include empty/missing-column tables and reject unexpected application tables/views.
            foreach (var row in await Rows(connection,
                "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'", ct))
                actual.Add(row[0], new Table());
            foreach (var row in await Rows(connection,
                "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'", ct))
                actual[row[0]].Columns.Add(row[1], new Column(row[2].ToLowerInvariant(), row[3] == "YES"));
            var indexes = await Rows(connection,
                "SELECT TABLE_NAME,INDEX_NAME,NON_UNIQUE,COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory' ORDER BY TABLE_NAME,INDEX_NAME,SEQ_IN_INDEX", ct);
            foreach (var group in indexes.GroupBy(x => (Table: x[0], Name: x[1])))
            {
                var columns = group.Select(x => x[3]).ToArray();
                if (group.Key.Name == "PRIMARY") actual[group.Key.Table].PrimaryKey = columns;
                else actual[group.Key.Table].Indexes.Add(group.Key.Name, new Index(group.First()[2] == "0", columns));
            }
            var keys = await Rows(connection,
                "SELECT k.TABLE_NAME,k.CONSTRAINT_NAME,k.REFERENCED_TABLE_NAME,k.COLUMN_NAME,k.REFERENCED_COLUMN_NAME,r.DELETE_RULE FROM information_schema.KEY_COLUMN_USAGE k JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON k.CONSTRAINT_SCHEMA=r.CONSTRAINT_SCHEMA AND k.TABLE_NAME=r.TABLE_NAME AND k.CONSTRAINT_NAME=r.CONSTRAINT_NAME WHERE k.CONSTRAINT_SCHEMA=DATABASE() AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.TABLE_NAME,k.CONSTRAINT_NAME,k.ORDINAL_POSITION", ct);
            foreach (var group in keys.GroupBy(x => (Table: x[0], Name: x[1])))
                actual[group.Key.Table].ForeignKeys.Add(group.Key.Name, new ForeignKey(group.First()[2],
                    group.Select(x => x[3]).ToArray(), group.Select(x => x[4]).ToArray(), NormalizeDelete(group.First()[5])));
        }
        finally { await db.Database.CloseConnectionAsync(); }

        var contractJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        // Schema identifiers/types only; never include data rows or connection configuration in diagnostics.
        RequireContract(contractJson, actualJson, contractJson == actualJson ? null : Difference(
            JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual), "schema"));
        // Exercise normal EF materialization, not just TCP connectivity or an authentication challenge.
        _ = await db.Users.AsNoTracking().Take(1).ToListAsync(ct);
        _ = await db.Roles.AsNoTracking().Take(1).ToListAsync(ct);
        _ = await db.Events.AsNoTracking().Take(1).ToListAsync(ct);
        _ = await db.OrganizerInvitations.AsNoTracking().Take(1).ToListAsync(ct);
        _ = await db.AuditRecords.AsNoTracking().Take(1).ToListAsync(ct);
        return new ReadyResult("ready", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contractJson))).ToLowerInvariant());
    }

    private static string NormalizeDelete(string value) => value.Replace(" ", "").ToUpperInvariant() switch
    {
        "NOACTION" or "RESTRICT" => "RESTRICT",
        var other => other
    };

    private static string Difference(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) return path + ": incompatible definition kind";
        if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(property.Name, out var other)) return path + ": missing " + property.Name;
                if (property.Value.GetRawText() != other.GetRawText()) return Difference(property.Value, other, path + "/" + property.Name);
            }
            return path + ": unexpected " + string.Join(",", actual.EnumerateObject().Where(x => !expected.TryGetProperty(x.Name, out _)).Select(x => x.Name));
        }
        if (expected.ValueKind == JsonValueKind.Array) return path + ": incompatible ordered columns";
        return path + ": expected " + expected.GetRawText() + ", found " + actual.GetRawText();
    }

    private static async Task<List<string[]>> Rows(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i))!;
            rows.Add(row);
        }
        return rows;
    }
}
