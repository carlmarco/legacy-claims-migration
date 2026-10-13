using Etl.Core.Discovery;
using Etl.Core.Mapping;
using Microsoft.Data.Sqlite;

namespace Etl.Tests;

public sealed class DiscoveryAndLengthTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("etl-discovery-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Db(string sql)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sqlite");
        using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void ProfilerFindsTheClassicLegacyDefects()
    {
        var path = Db("""
            CREATE TABLE T (ID INTEGER, CODE INTEGER, DT TEXT, AMT TEXT, SEX TEXT, DX1 TEXT, DX2 TEXT, DX3 TEXT, REF TEXT);
            INSERT INTO T VALUES
              (1, '0389',  '20080412',   '1200.00',   '1',    'a', 'b', 'c', 'X1'),
              (2, 'V5841', '04/12/2008', '$1,200.00', 'M',    'a', 'b', 'c', 'X2'),
              (3, '4019',  '4/12/08',    '(50.00)',   'male', 'a', 'b', 'c', 'X2'),
              (4, '01000', '2008-04-12', ' 75.5 ',    '2',    'a', 'b', 'c', 'X4');
            """);
        var t = Assert.Single(SchemaProfiler.Profile(path));
        var col = t.Columns.ToDictionary(c => c.Name);
        Assert.Contains("ID", t.CandidateKeys);      // on 4 rows every all-distinct column qualifies, hence "candidates"
        Assert.DoesNotContain("REF", t.CandidateKeys); // REF has a duplicate
        Assert.Contains("DX{n} (n = 1..3)", t.RepeatingGroups);
        Assert.Contains(SchemaProfiler.Flags(col["DT"]), f => f.Contains("mixed date formats"));
        Assert.Contains(SchemaProfiler.Flags(col["AMT"]), f => f.Contains("money as formatted text"));
        Assert.Contains(SchemaProfiler.Flags(col["CODE"]), f => f.Contains("INTEGER column holding text"));
        Assert.Equal(3, col["CODE"].StorageTypes["integer"]);                            // SQLite stripped '0389' -> 389
        Assert.Contains(SchemaProfiler.Flags(col["SEX"]), f => f.StartsWith("code domain:") && f.Contains("`male`"));
    }

    [Fact]
    public void NearUniqueColumnIsFlaggedAsPossibleDuplicates()
    {
        var values = string.Join(",", Enumerable.Range(1, 100).Select(i => $"({i}, 'C{(i == 100 ? 99 : i)}')"));
        var t = Assert.Single(SchemaProfiler.Profile(Db($"CREATE TABLE T (ID INTEGER, CLM TEXT); INSERT INTO T VALUES {values};")));
        Assert.Contains(SchemaProfiler.Flags(t.Columns[1]), f => f.Contains("near-unique: 1 repeated"));
    }

    [Fact]
    public void DdlLengthsParseVarcharCharAndLike()
    {
        var ddl = DdlLengths.Parse("""
            CREATE TABLE IF NOT EXISTS s.a (id varchar(20) NOT NULL, sex char(1), note text, -- comment varchar(99)
                                            n character varying(7));
            CREATE TABLE IF NOT EXISTS s.b (LIKE s.a INCLUDING CONSTRAINTS, extra varchar(3));
            """);
        Assert.Equal(new Dictionary<string, int> { ["id"] = 20, ["sex"] = 1, ["n"] = 7 }, ddl["a"]);
        Assert.Equal(4, ddl["b"].Count);
        Assert.Equal(3, ddl["b"]["extra"]);
    }

    [Fact]
    public void ValueLongerThanTargetColumnIsQuarantinedNotTruncated()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "Etl.slnx"))) repo = repo.Parent!;
        foreach (var f in new[] { "mapping.yaml", "target.sql" })
            File.Copy(Path.Combine(repo.FullName, "tests/fixtures/dental", f), Path.Combine(_dir, f));
        // Tighten one column in this test's copy of the DDL: 'Okafor-Lee' (10 chars) no longer fits.
        var ddlPath = Path.Combine(_dir, "target.sql");
        File.WriteAllText(ddlPath, File.ReadAllText(ddlPath).Replace("last_name text", "last_name varchar(6)"));
        File.Copy(Db("""
            CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT, ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
            INSERT INTO PATIENT_TBL VALUES (1, 'P001', 'Okafor-Lee', '03/14/1961', 'f', '02139', 'F', '2019-01-01'),
                                           (2, 'P002', 'Lind', '03/14/1961', 'm', '10001', 'F', '2019-01-01');
            CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT, PROC1 TEXT, FEE1 TEXT, PROC2 TEXT,
                                   FEE2 TEXT, PROC3 TEXT, FEE3 TEXT, PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
            """), Path.Combine(_dir, "legacy.sqlite"));
        var m = new MappingSpec(Path.Combine(_dir, "mapping.yaml"));
        var recs = m.Transform().ToList();
        var q = Assert.Single(recs, r => !r.IsRow);
        Assert.Equal("TOO_LONG", q.Reason);
        Assert.Contains("last_name: length 10 > 6", q.Detail);
        Assert.Single(recs, r => r.IsRow && r.Entity == "patient");               // P002 still loads
    }
}
