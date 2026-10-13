using System.Text.RegularExpressions;

namespace Etl.Core.Mapping;

/// <summary>Result of an in-memory run: survivors per entity, plus every quarantined record with its reason.</summary>
public sealed record InMemoryResult(
    Dictionary<string, List<object?[]>> Rows,
    List<TransformRecord> Quarantine,
    Dictionary<string, (long Staged, long Survivors, long Duplicates, long Orphans)> Stats,
    long Excluded);

/// <summary>
/// The whole pipeline without a database, for the browser demo: transform rows from in-memory tables, then a C#
/// rendering of the merge's semantics: newest version wins per key (ties on the higher record id), children keep
/// only rows from their parent's surviving legacy row (follow_parent), and rows whose parent is missing become
/// ORPHAN_FK quarantine. The production merge does this as set-based SQL in Postgres (Merge.cs); this version is
/// for small samples and is clearly labeled as such in the demo.
/// </summary>
public static class InMemoryPipeline
{
    private static readonly Regex SimpleExclude = new(@"^\s*(\w+)\s*=\s*'([^']*)'\s*$");

    /// <summary>The spec's SQL exclude predicate, evaluated in memory. Only the `COL = 'X'` form is supported here.</summary>
    public static Func<IReadOnlyDictionary<string, object?>, bool> ExcludePredicate(MappingSpec m)
    {
        if (m.Exclude is null) return _ => false;
        var match = SimpleExclude.Match(m.Exclude);
        if (!match.Success) throw new NotSupportedException($"in-memory exclude supports only COL = 'X', got: {m.Exclude}");
        var (col, val) = (match.Groups[1].Value, match.Groups[2].Value);
        return row => row.TryGetValue(col, out var v) && v is not null && Conversion.ConverterRegistry.Str(v) == val;
    }

    public static InMemoryResult Run(MappingSpec m, IReadOnlyDictionary<string, (IReadOnlyList<string> Header, IReadOnlyList<object?[]> Rows)> tables)
    {
        var excluded = ExcludePredicate(m);
        var staged = m.Entities.Keys.ToDictionary(e => e, _ => new List<object?[]>());
        var quarantine = new List<TransformRecord>();
        long excludedCount = 0;
        foreach (var (table, sources) in m.ByTable)
        {
            if (!tables.TryGetValue(table, out var t)) continue;
            foreach (var values in t.Rows)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < t.Header.Count; i++) row[t.Header[i]] = values[i];
                if (excluded(row)) { excludedCount++; continue; }
                foreach (var src in sources)
                    foreach (var rec in m.TransformRow(src, row))
                        if (rec.IsRow) staged[rec.Entity].Add(rec.Values!); else quarantine.Add(rec);
            }
        }

        var survivors = new Dictionary<string, List<object?[]>>();
        var stats = new Dictionary<string, (long, long, long, long)>();
        foreach (var name in m.LoadOrder())
        {
            var e = m.Entities[name];
            var idx = e.Output.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
            int I(string c) => idx[c];
            string Key(object?[] r, IEnumerable<string> cols) => string.Join("\x1f", cols.Select(c => Conversion.ConverterRegistry.Str(r[I(c)] ?? "\0")));
            // Newest version wins: order by _version, then legacy record id (the same tie-break as the SQL merge).
            static (string, long) Version(object?[] r, int v, int rec) => (r[v] as string ?? "", Convert.ToInt64(r[rec]));
            var (vi, ri, ti) = (I("_version"), I("_src_rec_id"), I("_src_table"));
            IEnumerable<object?[]> rows = staged[name];
            if (e.Survivorship.TryGetValue("follow_parent", out var fp) && fp is string parent)
            {
                // Children keep only rows emitted by the parent's surviving legacy row.
                var pe = m.Entities[parent];
                var pidx = pe.Output.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
                var winners = staged[parent]
                    .GroupBy(r => string.Join("\x1f", pe.Key.Select(k => Conversion.ConverterRegistry.Str(r[pidx[k]] ?? "\0"))))
                    .Select(g => g.MaxBy(r => Version(r, pidx["_version"], pidx["_src_rec_id"]))!)
                    .Select(r => (Conversion.ConverterRegistry.Str(r[pidx["_src_table"]]!), Convert.ToInt64(r[pidx["_src_rec_id"]])))
                    .ToHashSet();
                rows = rows.Where(r => winners.Contains((Conversion.ConverterRegistry.Str(r[ti]!), Convert.ToInt64(r[ri]))));
            }
            var kept = rows.GroupBy(r => Key(r, e.Key)).Select(g => g.MaxBy(r => Version(r, vi, ri))!).ToList();
            var dups = staged[name].Count - kept.Count;
            long orphans = 0;
            foreach (var (p, childCols) in e.References)
            {
                var pe = m.Entities[p];
                var pIdx = pe.Output.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
                var keys = survivors[p].Select(r => string.Join("\x1f", pe.Key.Select(k => Conversion.ConverterRegistry.Str(r[pIdx[k]] ?? "\0")))).ToHashSet();
                var orphaned = kept.Where(r => !keys.Contains(string.Join("\x1f", childCols.Select(c => Conversion.ConverterRegistry.Str(r[I(c)] ?? "\0"))))).ToList();
                foreach (var o in orphaned)
                    quarantine.Add(new TransformRecord("quarantine", name, Table: Conversion.ConverterRegistry.Str(o[ti]!), RecId: Convert.ToInt64(o[ri]),
                                                       Reason: "ORPHAN_FK", Detail: $"no {p} for ({string.Join(", ", childCols)})"));
                orphans += orphaned.Count;
                kept = kept.Except(orphaned).ToList();
            }
            survivors[name] = kept;
            stats[name] = (staged[name].Count, kept.Count + orphans, dups, orphans);
        }
        return new InMemoryResult(survivors, quarantine, stats, excludedCount);
    }
}
