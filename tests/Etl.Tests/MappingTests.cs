using Etl.Core.Mapping;
using Microsoft.Data.Sqlite;

namespace Etl.Tests;

/// <summary>
/// The engine against a second, unrelated legacy schema (the dental fixture shared with the Python tests):
/// proves the C# engine has no SynPUF assumptions. Ported from tests/test_mapping_generic.py.
/// </summary>
public sealed class MappingTests : IDisposable
{
    private static readonly string Repo = FindRepoRoot();
    private readonly string _dir = Directory.CreateTempSubdirectory("etl-dental-").FullName;
    private readonly MappingSpec _m;

    public MappingTests()
    {
        foreach (var f in new[] { "mapping.yaml", "target.sql" })
            File.Copy(Path.Combine(Repo, "tests/fixtures/dental", f), Path.Combine(_dir, f));
        using (var con = new SqliteConnection($"Data Source={Path.Combine(_dir, "legacy.sqlite")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT,
                                          ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
                INSERT INTO PATIENT_TBL VALUES
                  (1, 'p001 ', 'Okafor', '03/14/1961', 'f', '02139', 'F', '2019-01-01'),
                  (2, 'P002', 'Lind',   '00/00/0000', 'M', 10001,  NULL, '2019-01-02'),
                  (3, 'P003', 'Ruiz',   '13/40/1970', 'm', 94110,  'F', '2019-01-03'),
                  (4, 'P004', 'Gone',   '01/01/1950', 'm', 94110,  'T', '2019-01-04');
                CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT,
                                       PROC1 TEXT, FEE1 TEXT, PROC2 TEXT, FEE2 TEXT, PROC3 TEXT, FEE3 TEXT,
                                       PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
                INSERT INTO APPT_TBL VALUES
                  (1, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL, 'F', '2020-06-01');
                """;
            cmd.ExecuteNonQuery();
        }
        _m = new MappingSpec(Path.Combine(_dir, "mapping.yaml"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Etl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (Etl.slnx) not found");
    }

    private List<Dictionary<string, object?>> Rows(string entity) =>
        _m.Transform().Where(r => r.IsRow && r.Entity == entity)
          .Select(r => _m.Entities[entity].Output.Zip(r.Values!).ToDictionary(p => p.First, p => p.Second)).ToList();

    [Fact]
    public void FlatEntityConversions()
    {
        var pts = Rows("patient").ToDictionary(p => (string)p["patient_number"]!);
        Assert.Equal(["P001", "P002"], pts.Keys.Order());                      // P003 quarantined, P004 soft-deleted
        Assert.Equal("02139", pts["P001"]["zip"]);                              // INTEGER column dropped the zero; restored
        Assert.Equal(new DateOnly(1961, 3, 14), pts["P001"]["birth_date"]);
        Assert.Equal("F", pts["P001"]["gender"]);
        Assert.Null(pts["P002"]["birth_date"]);                                 // null token
        Assert.Equal("PATIENT_TBL", pts["P001"]["_src_table"]);
        Assert.Equal("2019-01-01", pts["P001"]["_version"]);
    }

    [Fact]
    public void BadValueIsQuarantinedNotNulled()
    {
        var q = Assert.Single(_m.Transform(), r => !r.IsRow);
        Assert.Equal(3L, q.RecId);
        Assert.Equal("BAD_VALUE", q.Reason);
        Assert.Contains("birth_date", q.Detail);
        Assert.Equal("P003", q.Raw!["PT_NUM"]);
    }

    [Fact]
    public void SoftDeletesExcludedAndCounted() => Assert.Equal(1, _m.ExcludedCount("PATIENT_TBL"));

    [Fact]
    public void UnpivotKeepsOnlyRealSlots()
    {
        var procs = Rows("appointment_procedure").Select(p => (p["slot"], p["procedure_code"], p["fee"])).ToList();
        Assert.Equal([(1L, "D0120", 45.00m), (2L, "D1110", 95.00m)], procs);  // slot 3 is 0.00 with no code; slot 4 empty
    }

    [Fact]
    public void EntityMetadataAndLoadOrder()
    {
        var e = _m.Entities["appointment_procedure"];
        Assert.Equal(["appointment_number", "slot"], e.Key);
        Assert.Equal(["appointment_number"], e.References["appointment"]);
        Assert.Equal(MappingSpec.LineageColumns, e.Output.TakeLast(3));
        Assert.Equal(["patient", "appointment", "appointment_procedure"], _m.LoadOrder());
    }

    [Fact]
    public void YamlTypingMatchesPyYaml()
    {
        var path = Path.Combine(_dir, "t.yaml");
        File.WriteAllText(path, """
            base: &b {x: 1}
            merged: {<<: *b, y: "1", z: true, w: N, n: ~, f: 0.5, q: "true"}
            """);
        var m = (IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)YamlLoader.LoadFile(path)!)["merged"]!;
        Assert.Equal(1L, m["x"]);          // merge key resolved
        Assert.Equal("1", m["y"]);         // quoted stays a string
        Assert.Equal(true, m["z"]);
        Assert.Equal("N", m["w"]);         // PyYAML: plain N is a string, not a bool
        Assert.Null(m["n"]);
        Assert.Equal(0.5, m["f"]);
        Assert.Equal("true", m["q"]);
        Assert.Equal(["x", "y", "z", "w", "n", "f", "q"], m.Keys);  // key order preserved
    }

    [Fact]
    [Trait("Category", "RequiresData")]
    public void SynpufReferenceSetsMatchPython()
    {
        var m = new MappingSpec(Path.Combine(Repo, "datasets/synpuf/mapping.yaml"));
        Assert.Equal(14662, m.Context.References["icd9_dx"].Count);   // same sizes the Python loader produced
        Assert.Equal(3888, m.Context.References["icd9_px"].Count);
    }
}
