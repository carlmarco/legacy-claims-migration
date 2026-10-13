using Microsoft.Data.Sqlite;

namespace Etl.Core.Mapping;

/// <summary>
/// The SQLite legacy source: reads a mapping's source database and feeds rows to the engine's TransformRow.
/// Extension methods, so call sites read `m.Transform(...)` while Etl.Engine itself stays free of SQLite
/// (and runs in the browser).
/// </summary>
public static class SqliteSource
{
    public static SqliteConnection OpenSource(this MappingSpec m)
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m.SourcePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        return con;
    }

    /// <summary>Soft-deleted rows excluded by the spec's exclude predicate (NULL-safe, like the transform's filter).</summary>
    public static long ExcludedCount(this MappingSpec m, string table)
    {
        if (m.Exclude is null) return 0;
        using var con = m.OpenSource();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE coalesce(({m.Exclude}), 0)";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The newest (version, rec_id) in a legacy table right now: the upper bound of a sync window.</summary>
    public static (string Version, long RecId)? HighMark(this MappingSpec m, string table)
    {
        using var con = m.OpenSource();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT {m.VersionColumn}, {m.RecIdColumn} FROM {table} ORDER BY {m.VersionColumn} DESC, {m.RecIdColumn} DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (System.Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!, r.GetInt64(1)) : null;
    }

    private static void AddWindow(MappingSpec m, SqliteCommand cmd, List<string> where, SyncWindow w)
    {
        // Row-value comparison: (version, rec_id) is a total order, so equal timestamps at the boundary can't be skipped.
        if (w.From is { } f)
        {
            where.Add($"({m.VersionColumn}, {m.RecIdColumn}) > ($fv, $fr)");
            cmd.Parameters.AddWithValue("$fv", f.Version);
            cmd.Parameters.AddWithValue("$fr", f.RecId);
        }
        where.Add($"({m.VersionColumn}, {m.RecIdColumn}) <= ($tv, $tr)");
        cmd.Parameters.AddWithValue("$tv", w.To.Version);
        cmd.Parameters.AddWithValue("$tr", w.To.RecId);
    }

    /// <summary>
    /// Target keys of rows soft-deleted inside the window: each one deletes the target row it stood for
    /// (if that row isn't newer). Rows whose keys don't convert are ignored: there's nothing to delete.
    /// </summary>
    public static IEnumerable<Tombstone> Tombstones(this MappingSpec m, string table, SyncWindow window)
    {
        if (m.Exclude is null) yield break;
        using var con = m.OpenSource();
        using var cmd = con.CreateCommand();
        var where = new List<string> { $"coalesce(({m.Exclude}), 0)" };
        AddWindow(m, cmd, where, window);
        cmd.CommandText = $"SELECT * FROM {table} WHERE {string.Join(" AND ", where)} ORDER BY {m.RecIdColumn}";
        using var reader = cmd.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var flat = m.ByTable[table].Where(s => s.Unpivot is null).ToList();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(names.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length; i++) row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            foreach (var src in flat)
                foreach (var rec in m.TransformRow(src, row).Where(r => r.IsRow))
                {
                    var e = m.Entities[src.Entity];
                    var key = e.Key.Select(k => rec.Values![e.Output.ToList().IndexOf(k)]).ToArray();
                    yield return new Tombstone(src.Entity, key, System.Convert.ToString(row[m.VersionColumn], System.Globalization.CultureInfo.InvariantCulture)!);
                }
        }
    }

    /// <summary>Stream every record for the given legacy tables (default: all), optionally one REC_ID range or sync window.</summary>
    public static IEnumerable<TransformRecord> Transform(this MappingSpec m, IEnumerable<string>? tables = null, (long Lo, long Hi)? recRange = null, long? limit = null,
                                                 IReadOnlyDictionary<string, SyncWindow>? windows = null)
    {
        using var con = m.OpenSource();
        foreach (var table in tables ?? m.ByTable.Keys)
        {
            using var cmd = con.CreateCommand();
            var where = new List<string>();
            // coalesce: a NULL flag must not exclude the row (NOT NULL is NULL in SQL).
            if (m.Exclude is not null) where.Add($"NOT coalesce(({m.Exclude}), 0)");
            if (recRange is { } r) where.Add($"{m.RecIdColumn} BETWEEN {r.Lo} AND {r.Hi}");
            if (windows is not null)
            {
                if (!windows.TryGetValue(table, out var w)) continue;   // nothing new in this table
                AddWindow(m, cmd, where, w);
            }
            cmd.CommandText = $"SELECT * FROM {table}" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                      + $" ORDER BY {m.RecIdColumn}" + (limit is { } l ? $" LIMIT {l}" : "");
            using var reader = cmd.ExecuteReader();
            var sources = m.ByTable[table];
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(names.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < names.Length; i++)
                    row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                foreach (var src in sources)
                    foreach (var rec in m.TransformRow(src, row))
                        yield return rec;
            }
        }
    }
}
