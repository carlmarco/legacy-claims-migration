using System.Net;
using System.Text;
using Npgsql;

namespace Etl.Core;

/// <summary>
/// Manual-review report from etl.quarantine: a CSV for whoever works the queue (one row per rejected record, with the
/// reason and detail) and an HTML summary grouped by entity and reason. In a dry run it's written inside the merge
/// transaction, before the rollback, so it shows what *would* be quarantined.
/// </summary>
public static class ReviewReport
{
    public static Dictionary<string, object?> Write(NpgsqlConnection con, NpgsqlTransaction tx, Mapping.MappingSpec m,
                                                    long runId, string dir, bool dryRun)
    {
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, $"{m.Name}_run{runId}{(dryRun ? "_dryrun" : "")}");
        var rows = new List<(string Entity, string Table, long RecId, string Reason, string? Detail)>();
        using (var cmd = new NpgsqlCommand("""
            SELECT entity, src_table, src_rec_id, reason, detail FROM etl.quarantine
            WHERE dataset = $1 ORDER BY reason, entity, src_table, src_rec_id
            """, con, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = m.Name });
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        var csv = new StringBuilder("entity,src_table,src_rec_id,reason,detail\n");
        foreach (var x in rows) csv.AppendLine(string.Join(",", Csv(x.Entity), Csv(x.Table), x.RecId, Csv(x.Reason), Csv(x.Detail)));
        File.WriteAllText(stem + ".csv", csv.ToString());

        var groups = rows.GroupBy(x => (x.Reason, x.Entity)).OrderByDescending(g => g.Count()).ToList();
        var html = new StringBuilder($$"""
            <!doctype html><meta charset="utf-8"><title>Review queue: {{m.Name}} run {{runId}}</title>
            <style>body{font:14px system-ui;margin:2rem;max-width:70rem}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.3rem .6rem;text-align:left}th{background:#f3f3f3}code{font-size:12px}</style>
            <h1>Review queue: {{m.Name}}, run {{runId}}{{(dryRun ? " (dry run: nothing was committed)" : "")}}</h1>
            <p>{{rows.Count:N0}} records need review. Each row below is a reason the engine refused to load data rather than guess.
            Full list: <code>{{Path.GetFileName(stem)}}.csv</code>.</p>
            <table><tr><th>Reason</th><th>Entity</th><th>Records</th><th>Example detail</th></tr>
            """);
        foreach (var g in groups)
            html.Append($"<tr><td>{g.Key.Reason}</td><td>{g.Key.Entity}</td><td>{g.Count():N0}</td><td><code>{WebUtility.HtmlEncode(g.First().Detail ?? "")}</code></td></tr>\n");
        html.Append("</table>\n");
        File.WriteAllText(stem + ".html", html.ToString());
        return new Dictionary<string, object?> { ["records"] = rows.Count, ["csv"] = stem + ".csv", ["html"] = stem + ".html" };
    }

    private static string Csv(string? s) =>
        s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
