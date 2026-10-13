using Etl.Core.IO;
using Etl.Core.Mapping;

namespace Etl.Tests;

/// <summary>
/// The browser demo's in-memory pipeline must agree with the real Postgres merge. These assertions mirror
/// PipelineTests (same dental fixture, same expected outcomes), run with no database at all.
/// </summary>
public class InMemoryAndOutputTests
{
    private static readonly string Repo = FindRepo();
    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "Etl.slnx"))) d = d.Parent!;
        return d.FullName;
    }

    private static MappingSpec DentalSpec() =>
        new(new InMemoryResolver(new Dictionary<string, byte[]>
        {
            ["mapping.yaml"] = File.ReadAllBytes(Path.Combine(Repo, "tests/fixtures/dental/mapping.yaml")),
            ["target.sql"] = File.ReadAllBytes(Path.Combine(Repo, "tests/fixtures/dental/target.sql")),
        }), "mapping.yaml");

    private const string Patients = """
        ROWID_,PT_NUM,LNAME,BDATE,GENDER,ZIP,DELETED,MODIFIED
        1,P001,Okafor,03/14/1961,f,2139,F,2019-01-01
        2,"p001 ",Okafor-Lee,03/14/1961,F,2139,F,2021-05-05
        3,P002,Lind,13/40/1970,m,10001,F,2019-01-02
        4,P003,Gone,01/01/1950,m,94110,T,2019-01-04
        """;
    private const string Appts = """
        ROWID_,APPT_NO,PT_NUM,APPT_DT,PROC1,FEE1,PROC2,FEE2,PROC3,FEE3,PROC4,FEE4,DELETED,MODIFIED
        1,500,P001,06/01/2020,D0120,$45.00,D1110,95,,0.00,,,F,2020-06-01
        2,500,P001,06/01/2020,D0120,$45.00,D1110,95,,0.00,,,F,2020-06-01
        3,501,P002,06/02/2020,D0120,45,,,,,,,F,2020-06-02
        """;

    private static InMemoryResult Run(string appts = Appts)
    {
        var tables = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<object?[]>)>();
        foreach (var (name, csv) in new[] { ("PATIENT_TBL", Patients), ("APPT_TBL", appts) })
        {
            var (h, rows) = Csv.Read(csv);
            tables[name] = (h, rows);
        }
        return InMemoryPipeline.Run(DentalSpec(), tables);
    }

    [Fact]
    public void AgreesWithThePostgresMergeOnTheDentalFixture()
    {
        var r = Run();
        var m = DentalSpec();
        var patient = Assert.Single(r.Rows["patient"]);
        Assert.Equal("P001", patient[0]);                                  // ID variant normalized
        Assert.Equal("Okafor-Lee", patient[1]);                            // newer version wins
        Assert.Equal("02139", patient[4]);                                 // zero restored from a CSV number
        Assert.Equal(1, r.Stats["patient"].Duplicates);
        Assert.Equal(1, r.Stats["appointment"].Duplicates);
        Assert.Equal(1, r.Stats["appointment"].Orphans);                   // its patient was quarantined (BAD_VALUE)
        Assert.Equal(1, r.Stats["appointment_procedure"].Orphans);         // and the orphan cascades
        Assert.Equal(1, r.Excluded);                                       // soft-deleted P003
        Assert.Equal(["BAD_VALUE", "ORPHAN_FK", "ORPHAN_FK"], r.Quarantine.Select(q => q.Reason!).Order());
        var slots = r.Rows["appointment_procedure"].Select(p => (p[1], p[2], p[3])).OrderBy(x => (long)x.Item1!).ToList();
        Assert.Equal([(1L, "D0120", 45.00m), (2L, "D1110", 95.00m)], slots);
    }

    [Fact]
    public void ChildSlotDroppedByNewerParentVersionIsNotRevived()
    {
        var r = Run(Appts + "\n4,500,P001,06/01/2020,D0120,$45.00,,0.00,,0.00,,,F,2020-07-01\n");
        Assert.Equal([1L], r.Rows["appointment_procedure"].Select(p => (long)p[1]!));   // D22, in memory too
    }

    [Fact]
    public void CsvRoundTripsQuotesCommasAndNewlines()
    {
        var (h, rows) = Csv.Read("a,b\n\"x, \"\"y\"\"\",\"line1\nline2\"\n,z\n");
        Assert.Equal(["a", "b"], h);
        Assert.Equal("x, \"y\"", rows[0][0]);
        Assert.Equal("line1\nline2", rows[0][1]);
        Assert.Null(rows[1][0]);
        Assert.Equal("\"x, \"\"y\"\"\"", Csv.Field("x, \"y\""));
    }

    [Fact]
    public void SqlScriptsUseEachDialectsQuotingAndLiterals()
    {
        object?[] row = ["O'Brien", new DateOnly(2008, 4, 12), true, 12.50m, null];
        string[] cols = ["name", "dob", "esrd", "amt", "note"];
        var pg = OutputWriters.ToSql(SqlDialect.Postgres, "synpuf", "beneficiary", cols, [row]);
        var ts = OutputWriters.ToSql(SqlDialect.TSql, "synpuf", "beneficiary", cols, [row]);
        Assert.Contains("INSERT INTO \"synpuf\".\"beneficiary\" (\"name\", \"dob\", \"esrd\", \"amt\", \"note\")", pg);
        Assert.Contains("('O''Brien', DATE '2008-04-12', TRUE, 12.50, NULL)", pg);
        Assert.Contains("INSERT INTO [synpuf].[beneficiary] ([name], [dob], [esrd], [amt], [note])", ts);
        Assert.Contains("(N'O''Brien', CAST('2008-04-12' AS DATE), 1, 12.50, NULL)", ts);
    }

    [Fact]
    public void SqlScriptBatchesStayUnderTheTSqlRowLimit()
    {
        var rows = Enumerable.Range(0, 1201).Select(i => new object?[] { (long)i }).ToList();
        var sql = OutputWriters.ToSql(SqlDialect.TSql, "s", "t", ["id"], rows);
        Assert.Equal(3, sql.Split("INSERT INTO").Length - 1);              // 500 + 500 + 201
    }
}

/// <summary>The demo's bundled sample, loaded exactly as the browser does (in-memory files, CSV tables).</summary>
public class DemoSampleTests
{
    [Fact]
    public void BundledSampleMigratesInMemory()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "Etl.slnx"))) d = d.Parent!;
        var root = Path.Combine(d.FullName, "demo/Etl.Demo/wwwroot/sample");
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes);
        var m = new MappingSpec(new InMemoryResolver(files), "mapping.yaml");
        var tables = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<object?[]>)>();
        foreach (var t in m.ByTable.Keys)
        {
            var (h, rows) = Csv.Read(System.Text.Encoding.UTF8.GetString(files[$"tables/{t}.csv"]));
            tables[t] = (h, rows);
        }
        var r = InMemoryPipeline.Run(m, tables);
        Assert.DoesNotContain(r.Quarantine, q => q.Reason == "BAD_VALUE");           // every injected format defect repaired
        Assert.Contains(r.Quarantine, q => q.Reason == "ORPHAN_FK");                 // the two removed beneficiaries
        Assert.True(r.Excluded > 0);                                                 // soft-deleted junk rows
        Assert.True(r.Stats["claim"].Duplicates > 0);                                // reloads resolved
        Assert.True(r.Rows["claim_diagnosis"].Count > 1000);
    }
}
