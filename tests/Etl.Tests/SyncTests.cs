using Etl.Core;
using Etl.Core.Load;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Etl.Tests;

/// <summary>Incremental sync on the dental fixture: watermarks, deltas, version guard, soft-delete tombstones.</summary>
[Trait("Category", "RequiresPostgres")]
[Collection("postgres")]
public sealed class SyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("etl-sync-").FullName;
    private string MappingPath => Path.Combine(_dir, "mapping.yaml");

    public SyncTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "Etl.slnx"))) repo = repo.Parent!;
        foreach (var f in new[] { "mapping.yaml", "target.sql" })
            File.Copy(Path.Combine(repo.FullName, "tests/fixtures/dental", f), Path.Combine(_dir, f));
        Sqlite("""
            CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT, ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
            INSERT INTO PATIENT_TBL VALUES (1, 'P001', 'Okafor', '03/14/1961', 'f', '02139', 'F', '2020-01-01 09:00:00');
            CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT, PROC1 TEXT, FEE1 TEXT, PROC2 TEXT, FEE2 TEXT,
                                   PROC3 TEXT, FEE3 TEXT, PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
            INSERT INTO APPT_TBL VALUES
              (1, '500', 'P001', '06/01/2020', 'D0120', '45', 'D1110', '95', NULL, NULL, NULL, NULL, 'F', '2020-06-01 09:00:00'),
              (2, '501', 'P001', '06/02/2020', 'D0140', '60', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-06-02 09:00:00');
            """);
        using var ds = Pg.DataSource();
        using var con = ds.OpenConnection();
        Pg.Exec(con, "DROP SCHEMA IF EXISTS dental CASCADE");
        if (Pg.Scalar<bool>(con, "SELECT to_regclass('etl.sync_watermark') IS NOT NULL"))
            Pg.Exec(con, "DELETE FROM etl.sync_watermark WHERE dataset = 'dental'");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Sqlite(string sql)
    {
        using var con = new SqliteConnection($"Data Source={Path.Combine(_dir, "legacy.sqlite")};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<object?[]> Q(string sql)
    {
        using var ds = Pg.DataSource();
        using var con = ds.OpenConnection();
        using var cmd = new NpgsqlCommand(sql, con);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row); }
        return rows;
    }

    private Dictionary<string, object?> Sync(bool dryRun = false) =>
        Pipeline.Run(new RunOptions(MappingPath, "batched", DryRun: dryRun, Incremental: true, ReviewDir: null));

    private static List<object?[]> Watermarks() =>
        Q("SELECT src_table, last_version, last_rec_id FROM etl.sync_watermark WHERE dataset = 'dental' ORDER BY 1");

    [Fact]
    public void FirstSyncLoadsEverythingAndSetsWatermarks()
    {
        var s = Sync();
        Assert.Equal(6L, s["staged_rows"]);   // 1 patient + 2 appointments + 3 procedure slots
        Assert.Equal([["APPT_TBL", "2020-06-02 09:00:00", 2L], ["PATIENT_TBL", "2020-01-01 09:00:00", 1L]], Watermarks());
    }

    [Fact]
    public void SyncWithNoChangesExtractsNothing()
    {
        Sync();
        var s = Sync();
        Assert.Equal(0L, s["staged_rows"]);
        Assert.Equal(2L, Q("SELECT count(*) FROM dental.appointment").Single()[0]);
    }

    [Fact]
    public void SyncAppliesOnlyTheDelta_UpdatesInsertsAndSoftDeletes()
    {
        Sync();
        Sqlite("""
            INSERT INTO APPT_TBL VALUES
              (3, '500', 'P001', '06/01/2020', 'D0120', '50', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-07-01 09:00:00'),  -- new version of 500: fee 45->50, slot 2 removed
              (4, '502', 'P001', '06/03/2020', 'D2330', '120', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-07-02 09:00:00'), -- new appointment
              (5, '501', 'P001', '06/02/2020', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'T', '2020-07-03 09:00:00');     -- 501 soft-deleted
            """);
        var s = Sync();
        Assert.Equal(4L, s["staged_rows"]);   // only the new rows: v2 of 500 + its slot, 502 + its slot; the deleted row isn't staged
        Assert.Equal([[500, 50.00m], [502, 120.00m]],
                     Q("SELECT a.appointment_number, p.fee FROM dental.appointment a JOIN dental.appointment_procedure p USING (appointment_number) ORDER BY 1, slot"));
        var syncStats = (Dictionary<string, object?>)((Dictionary<string, object?>)s["merge"]!)["_sync"]!;
        Assert.Equal(1, syncStats["tombstones"]);
        Assert.Equal(["APPT_TBL", "2020-07-03 09:00:00", 5L], Watermarks()[0]);
    }

    [Fact]
    public void DryRunSyncNeverAdvancesTheWatermark()
    {
        Sync();
        Sqlite("INSERT INTO APPT_TBL VALUES (3, '503', 'P001', '06/05/2020', 'D0120', '45', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-08-01 09:00:00')");
        var before = Watermarks();
        var s = Sync(dryRun: true);
        Assert.Equal(2L, s["staged_rows"]);           // it saw the delta...
        Assert.Equal(before, Watermarks());           // ...but committed nothing, so the next real sync sees it again
        Assert.Equal(2L, Sync()["staged_rows"]);
    }

    [Fact]
    public void RowWithTheSameTimestampAsTheWatermarkIsNotSkipped()
    {
        Sync();
        // Same MODIFIED as the current watermark row (rec 2) but a higher REC_ID: a timestamp-only watermark would skip it.
        Sqlite("INSERT INTO APPT_TBL VALUES (3, '504', 'P001', '06/06/2020', 'D0120', '45', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-06-02 09:00:00')");
        Assert.Equal(2L, Sync()["staged_rows"]);
        Assert.Single(Q("SELECT 1 FROM dental.appointment WHERE appointment_number = 504"));
    }
}
