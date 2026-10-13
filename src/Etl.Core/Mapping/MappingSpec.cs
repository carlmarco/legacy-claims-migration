using Etl.Core.Conversion;
using Microsoft.Data.Sqlite;

namespace Etl.Core.Mapping;

/// <summary>One target column: a source column through a converter, or a constant. Emits one or more outputs.</summary>
public sealed record Column(string Name, string? Source, Converter? Convert, object? Const, IReadOnlyList<string> Outputs);

/// <summary>Repeating groups (DX1..DX10, L{n}_PMT) or a delimited column, turned into one row per slot.</summary>
public sealed record Unpivot(
    string Index,
    IReadOnlyList<(string Field, string Template)>? Fields,
    int N,
    string? SplitColumn,
    string SplitSep,
    string? SplitField,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<string> KeepIfAny);

public sealed class CompiledSource(string entity, string table, IReadOnlyList<Column> columns, Unpivot? unpivot,
                                   IReadOnlyList<(string From, string Thru)> swapIfReversed, List<string> output)
{
    public string Entity { get; } = entity;
    public string Table { get; } = table;
    public IReadOnlyList<Column> Columns { get; } = columns;
    public Unpivot? Unpivot { get; } = unpivot;
    public IReadOnlyList<(string From, string Thru)> SwapIfReversed { get; } = swapIfReversed;
    public List<string> Output { get; set; } = output;
}

public sealed record Entity(string Name, IReadOnlyList<string> Key, IReadOnlyDictionary<string, object?> Survivorship,
                            IReadOnlyDictionary<string, IReadOnlyList<string>> References, IReadOnlyList<string> Output);

/// <summary>A transformed target row, or a legacy row routed to quarantine.</summary>
public sealed record TransformRecord(string Kind, string Entity, object?[]? Values = null, string? Table = null,
                                     long? RecId = null, string? Reason = null, string? Detail = null,
                                     IReadOnlyDictionary<string, object?>? Raw = null)
{
    public bool IsRow => Kind == "row";
}

/// <summary>
/// A dataset's mapping spec (YAML), compiled into row-level transforms. Dataset-agnostic: everything about the
/// source and target lives in the spec. Row-local work happens here; set-based work (dedup, survivorship, FK
/// checks) is declared in the spec and executed as SQL in the merge (DECISIONS D14). Mirrors src/etl/mapping.py.
/// </summary>
public sealed class MappingSpec
{
    public static readonly IReadOnlyList<string> LineageColumns = ["_src_table", "_src_rec_id", "_version"];

    public string Path { get; }
    public string BaseDir { get; }
    public string Name { get; }
    public string SourcePath { get; }
    public string TargetSchema { get; }
    public string TargetDdlPath { get; }
    public string RecIdColumn { get; }
    public string VersionColumn { get; }
    public string? Exclude { get; }
    public IReadOnlySet<string> NullTokens { get; }
    public ConverterContext Context { get; }
    public OrderedDictionary<string, Entity> Entities { get; } = new();
    public OrderedDictionary<string, List<CompiledSource>> ByTable { get; } = new();

    /// <summary>Per entity: output index -> max length, from the target DDL's varchar(n)/char(n).</summary>
    public Dictionary<string, Dictionary<int, (string Column, int Max)>> LengthLimits { get; } = new();

    public MappingSpec(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        BaseDir = System.IO.Path.GetDirectoryName(Path)!;
        var spec = Map(YamlLoader.LoadFile(Path));
        Name = (string)spec["name"]!;
        SourcePath = Resolve((string)Map(spec["source"])["path"]!);
        var target = Map(spec["target"]);
        TargetSchema = (string)target["schema"]!;
        TargetDdlPath = Resolve((string)target["ddl"]!);
        var lineage = Map(spec["lineage"]);
        RecIdColumn = (string)lineage["rec_id"]!;
        VersionColumn = (string)lineage["version"]!;
        Exclude = spec.TryGetValue("exclude", out var ex) ? ex as string : null;
        NullTokens = (spec.TryGetValue("null_tokens", out var nt) && nt is List<object?> tokens
            ? tokens.Select(t => (string)t!) : [""]).ToHashSet();

        var codeMaps = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        if (spec.TryGetValue("code_maps", out var cm) && cm is not null)
            foreach (var (name, m) in Map(cm)) codeMaps[name] = BuildCodeMap(Map(m));
        var refs = new Dictionary<string, IReadOnlySet<string>>();
        if (spec.TryGetValue("references", out var rf) && rf is not null)
            foreach (var (name, r) in Map(rf)) refs[name] = Mapping.References.Load(Map(r), BaseDir);
        Context = new ConverterContext(codeMaps, refs);

        foreach (var (entityName, e) in Map(spec["entities"]))
        {
            var em = Map(e);
            var compiled = ((List<object?>)em["sources"]!).Select(s => CompileSource(entityName, Map(s))).ToList();
            if (compiled.Any(c => c.Unpivot is not null))
            {
                if (compiled.Select(c => string.Join(",", c.Output)).Distinct().Count() != 1)
                    throw new InvalidDataException($"entity {entityName}: unpivot sources produce different columns");
            }
            else
            {
                // Flat entities: a source may omit columns another source has; they become NULL.
                var union = compiled.SelectMany(c => c.Output).Where(c => !LineageColumns.Contains(c)).Distinct().ToList();
                foreach (var c in compiled) c.Output = [.. union, .. LineageColumns];
            }
            var references = new Dictionary<string, IReadOnlyList<string>>();
            if (em.TryGetValue("references", out var refsSpec) && refsSpec is not null)
                foreach (var (parent, cols) in Map(refsSpec)) references[parent] = Strings(cols);
            Entities[entityName] = new Entity(entityName, Strings(em["key"]),
                em.TryGetValue("survivorship", out var sv) && sv is not null ? Map(sv) : new Dictionary<string, object?>(),
                references, compiled[0].Output);
            foreach (var c in compiled)
            {
                if (!ByTable.TryGetValue(c.Table, out var list)) ByTable[c.Table] = list = [];
                list.Add(c);
            }
        }

        // Length limits come from the target DDL: one source of truth for what fits.
        if (File.Exists(TargetDdlPath))
        {
            var ddl = DdlLengths.Parse(File.ReadAllText(TargetDdlPath));
            foreach (var (name, e) in Entities)
            {
                var limits = new Dictionary<int, (string, int)>();
                if (ddl.TryGetValue(name, out var cols))
                    for (var i = 0; i < e.Output.Count; i++)
                        if (cols.TryGetValue(e.Output[i], out var max)) limits[i] = (e.Output[i], max);
                LengthLimits[name] = limits;
            }
        }
    }

    /// <summary>First string value longer than its target column allows, or null. Checked before load so one long
    /// value quarantines one row instead of failing a whole COPY batch (and is never silently truncated).</summary>
    private string? TooLong(string entity, object?[] values)
    {
        if (!LengthLimits.TryGetValue(entity, out var limits)) return null;
        foreach (var (i, (col, max)) in limits)
            if (values[i] is string s && s.Length > max)
                return $"{col}: length {s.Length} > {max} ('{(s.Length > 40 ? s[..40] + "…" : s)}')";
        return null;
    }

    // ---- compile ----------------------------------------------------------------------------------

    private string Resolve(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDir, relative));

    private static IReadOnlyDictionary<string, object?> Map(object? o) =>
        o as IReadOnlyDictionary<string, object?> ?? throw new InvalidDataException($"expected a mapping, got {o?.GetType().Name ?? "null"}");

    private static IReadOnlyList<string> Strings(object? o) => ((List<object?>)o!).Select(x => (string)x!).ToList();

    private IReadOnlyDictionary<string, object?> BuildCodeMap(IReadOnlyDictionary<string, object?> m)
    {
        var map = new Dictionary<string, object?>();
        if (m.TryGetValue("file", out var file) && file is string f)
        {
            var pairs = Map(YamlLoader.LoadFile(Resolve(f))).ToDictionary(kv => kv.Key, kv => ConverterRegistry.Str(kv.Value!));
            if (m.TryGetValue("invert", out var inv) && inv is true)
                pairs = pairs.ToDictionary(kv => kv.Value, kv => kv.Key);
            foreach (var (k, v) in pairs) map[k] = v;
            if (m.TryGetValue("include_targets", out var it) && it is true)
                foreach (var v in pairs.Values) map[v] = v;
        }
        if (m.TryGetValue("values", out var values) && values is not null)
            foreach (var (k, v) in Map(values)) map[k] = v;
        if (m.TryGetValue("case_insensitive", out var ci) && ci is true)
            foreach (var (k, v) in map.ToList()) map[k.ToLowerInvariant()] = v;
        return map;
    }

    private Column CompileColumn(string name, object? spec)
    {
        if (spec is IReadOnlyDictionary<string, object?> d && d.ContainsKey("const"))
            return new Column(name, null, null, d["const"], [""]);
        var parts = (List<object?>)spec!;
        var src = (string)parts[0]!;
        var conv = (string)parts[1]!;
        var args = parts.Count > 2 ? Map(parts[2]) : null;
        return new Column(name, src, ConverterRegistry.Build(conv, args, Context), null, ConverterRegistry.Outputs(conv));
    }

    private CompiledSource CompileSource(string entity, IReadOnlyDictionary<string, object?> s)
    {
        var columns = s.TryGetValue("columns", out var cols) && cols is not null
            ? Map(cols).Select(kv => CompileColumn(kv.Key, kv.Value)).ToList() : [];
        Unpivot? up = null;
        if (s.TryGetValue("unpivot", out var u) && u is not null)
        {
            var um = Map(u);
            var split = um.TryGetValue("split", out var sp) && sp is not null ? Map(sp) : null;
            up = new Unpivot(
                Index: (string)um["index"]!,
                Fields: um.TryGetValue("fields", out var f) && f is not null ? Map(f).Select(kv => (kv.Key, (string)kv.Value!)).ToList() : null,
                N: um.TryGetValue("n", out var n) && n is not null ? (int)(long)n : 0,
                SplitColumn: split?["column"] as string,
                SplitSep: split is not null && split.TryGetValue("sep", out var sep) && sep is string ss ? ss : "|",
                SplitField: split?["field"] as string,
                Columns: Map(um["columns"]).Select(kv => CompileColumn(kv.Key, kv.Value)).ToList(),
                KeepIfAny: um.TryGetValue("keep_if_any", out var k) && k is not null ? Strings(k) : []);
        }
        var output = columns.SelectMany(c => c.Outputs.Select(suf => c.Name + suf)).ToList();
        if (up is not null)
            output = [.. output, up.Index, .. up.Columns.SelectMany(c => c.Outputs.Select(suf => c.Name + suf))];
        output.AddRange(LineageColumns);
        var swaps = new List<(string, string)>();
        if (s.TryGetValue("rules", out var rules) && rules is not null
            && Map(rules).TryGetValue("swap_if_reversed", out var sw) && sw is List<object?> pairs)
            swaps.AddRange(pairs.Select(p => { var l = (List<object?>)p!; return ((string)l[0]!, (string)l[1]!); }));
        return new CompiledSource(entity, (string)s["from"]!, columns, up, swaps, output);
    }

    /// <summary>Entities ordered so every parent is merged before its children.</summary>
    public IReadOnlyList<string> LoadOrder()
    {
        var order = new List<string>();
        var visiting = new HashSet<string>();
        void Visit(string name)
        {
            if (order.Contains(name)) return;
            if (!visiting.Add(name)) throw new InvalidDataException($"reference cycle at {name}");
            foreach (var parent in Entities[name].References.Keys) Visit(parent);
            visiting.Remove(name);
            order.Add(name);
        }
        foreach (var name in Entities.Keys) Visit(name);
        return order;
    }

    // ---- run --------------------------------------------------------------------------------------

    private bool IsNull(object? v) => v is null || (v is string s && NullTokens.Contains(s.Trim()));

    /// <summary>Python truthiness for keep_if_any: null, 0, false and "" are falsy.</summary>
    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        decimal d => d != 0,
        string s => s.Length > 0,
        _ => true,
    };

    private void Apply(IReadOnlyList<Column> cols, Func<string, object?> lookup, Dictionary<string, object?> into)
    {
        foreach (var c in cols)
        {
            if (c.Source is null) { into[c.Name] = c.Const; continue; }
            var v = lookup(c.Source);
            if (IsNull(v))
            {
                foreach (var suf in c.Outputs) into[c.Name + suf] = null;
                continue;
            }
            object? r;
            try { r = c.Convert!(v!); }
            catch (ConversionException e) { throw new ConversionException($"{c.Name} <- {c.Source}: {e.Message}"); }
            if (r is CodeResult code)
            {
                into[c.Name] = code.Code;
                into[c.Name + "_raw"] = code.Raw;
                into[c.Name + "_resolution"] = code.Resolution;
            }
            else into[c.Name] = r;
        }
    }

    /// <summary>Transform one legacy row for one compiled source. Yields target rows and/or quarantine records.</summary>
    public IEnumerable<TransformRecord> TransformRow(CompiledSource src, IReadOnlyDictionary<string, object?> row)
    {
        var recId = Convert.ToInt64(row[RecIdColumn]);
        var lineage = new object?[] { src.Table, recId, row[VersionColumn] };
        var head = new Dictionary<string, object?>();
        string? error = null;
        try { Apply(src.Columns, k => row[k], head); }
        catch (ConversionException e) { error = e.Message; }
        if (error is not null)
        {
            yield return new TransformRecord("quarantine", src.Entity, Table: src.Table, RecId: recId,
                                             Reason: "BAD_VALUE", Detail: error, Raw: row);
            yield break;
        }
        foreach (var (from, thru) in src.SwapIfReversed)
            if (head.GetValueOrDefault(from) is DateOnly a && head.GetValueOrDefault(thru) is DateOnly b && b < a)
                (head[from], head[thru]) = (b, a);

        var dataCount = src.Output.Count - LineageColumns.Count;
        if (src.Unpivot is null)
        {
            var values = new object?[src.Output.Count];
            for (var i = 0; i < dataCount; i++) values[i] = head.GetValueOrDefault(src.Output[i]);
            lineage.CopyTo(values, dataCount);
            if (TooLong(src.Entity, values) is { } tooLong)
                yield return new TransformRecord("quarantine", src.Entity, Table: src.Table, RecId: recId,
                                                 Reason: "TOO_LONG", Detail: tooLong, Raw: row);
            else
                yield return new TransformRecord("row", src.Entity, values);
            yield break;
        }

        var u = src.Unpivot;
        List<Dictionary<string, object?>> slots;
        if (u.SplitColumn is not null)
        {
            var raw = row[u.SplitColumn];
            slots = IsNull(raw) ? [] : ConverterRegistry.Str(raw!).Split(u.SplitSep)
                .Select(p => new Dictionary<string, object?> { [u.SplitField!] = p }).ToList();
        }
        else
        {
            slots = Enumerable.Range(1, u.N).Select(i => u.Fields!.ToDictionary(
                f => f.Field, f => row[f.Template.Replace("{n}", i.ToString(System.Globalization.CultureInfo.InvariantCulture))])).ToList();
        }
        var headValues = src.Columns.SelectMany(c => c.Outputs.Select(suf => head[c.Name + suf])).ToArray();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Values.All(IsNull)) continue;
            var vals = new Dictionary<string, object?>();
            string? slotError = null;
            try { Apply(u.Columns, k => slot.TryGetValue(k, out var sv) ? sv : row[k], vals); }
            catch (ConversionException e) { slotError = e.Message; }
            if (slotError is not null)
            {
                yield return new TransformRecord("quarantine", src.Entity, Table: src.Table, RecId: recId,
                                                 Reason: "BAD_VALUE", Detail: $"[{u.Index}={i + 1}] {slotError}", Raw: row);
                continue;
            }
            if (u.KeepIfAny.Count > 0 && !u.KeepIfAny.Any(k => Truthy(vals.GetValueOrDefault(k)))) continue;
            var body = u.Columns.SelectMany(c => c.Outputs.Select(suf => vals[c.Name + suf]));
            object?[] slotValues = [.. headValues, (long)(i + 1), .. body, .. lineage];
            if (TooLong(src.Entity, slotValues) is { } slotTooLong)
                yield return new TransformRecord("quarantine", src.Entity, Table: src.Table, RecId: recId,
                                                 Reason: "TOO_LONG", Detail: $"[{u.Index}={i + 1}] {slotTooLong}", Raw: row);
            else
                yield return new TransformRecord("row", src.Entity, slotValues);
        }
    }

    public SqliteConnection OpenSource()
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SourcePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        return con;
    }

    /// <summary>Soft-deleted rows excluded by the spec's exclude predicate (NULL-safe, like the transform's filter).</summary>
    public long ExcludedCount(string table)
    {
        if (Exclude is null) return 0;
        using var con = OpenSource();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE coalesce(({Exclude}), 0)";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Stream every record for the given legacy tables (default: all), optionally one REC_ID range.</summary>
    public IEnumerable<TransformRecord> Transform(IEnumerable<string>? tables = null, (long Lo, long Hi)? recRange = null, long? limit = null)
    {
        using var con = OpenSource();
        foreach (var table in tables ?? ByTable.Keys)
        {
            var where = new List<string>();
            // coalesce: a NULL flag must not exclude the row (NOT NULL is NULL in SQL).
            if (Exclude is not null) where.Add($"NOT coalesce(({Exclude}), 0)");
            if (recRange is { } r) where.Add($"{RecIdColumn} BETWEEN {r.Lo} AND {r.Hi}");
            var sql = $"SELECT * FROM {table}" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                      + $" ORDER BY {RecIdColumn}" + (limit is { } l ? $" LIMIT {l}" : "");
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var sources = ByTable[table];
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(names.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < names.Length; i++)
                    row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                foreach (var src in sources)
                    foreach (var rec in TransformRow(src, row))
                        yield return rec;
            }
        }
    }
}
