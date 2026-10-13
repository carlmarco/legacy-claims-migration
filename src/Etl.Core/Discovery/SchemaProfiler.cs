using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Etl.Core.Discovery;

/// <summary>
/// Profiles a database you know nothing about: tables, declared vs stored types, null and blank rates,
/// distinct counts, value formats, candidate keys, and repeating column groups that should become child
/// tables. One streaming pass per table; memory is bounded by the per-column distinct cap.
/// </summary>
public static class SchemaProfiler
{
    public static List<TableProfile> Profile(string sqlitePath)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        var tables = Query(con, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .Select(r => (string)r[0]!).ToList();
        var result = new List<TableProfile>();
        foreach (var table in tables)
        {
            var cols = Query(con, $"PRAGMA table_info(\"{table}\")").Select(r => new ColumnProfile((string)r[1]!, (string)r[2]!)).ToList();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM \"{table}\"";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    for (var i = 0; i < cols.Count; i++) cols[i].Observe(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            var rows = cols.Count > 0 ? cols[0].Rows : 0;
            result.Add(new TableProfile(table, rows, cols, CandidateKeys(con, table, cols, rows), Profiling.RepeatingGroups(cols)));
        }
        return result;
    }

    private static List<object?[]> Query(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A column is a candidate key if it's never null and every value is distinct (exact check in SQL when capped).</summary>
    private static List<string> CandidateKeys(SqliteConnection con, string table, List<ColumnProfile> cols, long rows)
    {
        var keys = new List<string>();
        foreach (var c in cols.Where(c => c.Nulls == 0 && c.Blanks == 0 && rows > 0))
        {
            if (!c.DistinctCapped) { if (c.Distinct == rows) keys.Add(c.Name); continue; }
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT count(DISTINCT \"{c.Name}\") FROM \"{table}\"";
            c.ExactDistinct = (long)cmd.ExecuteScalar()!;
            if (c.ExactDistinct == rows) keys.Add(c.Name);
        }
        return keys;
    }

    public static string ToMarkdown(string source, IReadOnlyList<TableProfile> tables) => Profiling.ToMarkdown(source, tables);
    public static List<string> Flags(ColumnProfile c) => Profiling.Flags(c);
}
