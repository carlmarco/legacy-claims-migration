using Etl.Core;
using Etl.Core.Load;
using Etl.Core.Mapping;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Etl.Tests;

/// <summary>
/// End-to-end runs (staging -> survivorship -> orphans -> upsert) against real Postgres, on the dental fixture.
/// Ported from tests/test_pipeline.py. Needs `docker compose up -d`.
/// </summary>
[Trait("Category", "RequiresPostgres")]
[Collection("postgres")]   // these tests share the dental schema: run them one at a time
public sealed class PipelineTests : IDisposable
{
    private const string Legacy = """
        CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT, ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
        INSERT INTO PATIENT_TBL VALUES
          (1, 'P001', 'Okafor', '03/14/1961', 'f', '02139', 'F', '2019-01-01'),
          (2, 'p001 ', 'Okafor-Lee', '03/14/1961', 'F', '02139', 'F', '2021-05-05'),
          (3, 'P002', 'Lind', '13/40/1970', 'm', 10001, 'F', '2019-01-02'),
          (4, 'P003', 'Gone', '01/01/1950', 'm', 94110, 'T', '2019-01-04');
        CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT, PROC1 TEXT, FEE1 TEXT, PROC2 TEXT, FEE2 TEXT,
                               PROC3 TEXT, FEE3 TEXT, PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
        INSERT INTO APPT_TBL VALUES
          (1, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL, 'F', '2020-06-01'),
          (2, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL, 'F', '2020-06-01'),
          (3, '501', 'P002', '06/02/2020', 'D0120', '45', NULL, NULL, NULL, NULL, NULL, NULL, 'F', '2020-06-02');
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("etl-pipeline-").FullName;
    private string MappingPath => Path.Combine(_dir, "mapping.yaml");

    public PipelineTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "Etl.slnx"))) repo = repo.Parent!;
        foreach (var f in new[] { "mapping.yaml", "target.sql" })
            File.Copy(Path.Combine(repo.FullName, "tests/fixtures/dental", f), Path.Combine(_dir, f));
        Sqlite(Legacy);
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

    private Dictionary<string, object?> Run(bool fresh = false, bool dryRun = false, bool deferFks = false) =>
        Pipeline.Run(new RunOptions(MappingPath, "batched", DryRun: dryRun, Fresh: fresh, DeferFks: deferFks, ReviewDir: null));

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> s, string entity) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)s["merge"]!)[entity]!;

    [Fact]
    public void FreshRun()
    {
        var s = Run(fresh: true);
        Assert.Equal(1L, Merge(s, "patient")["duplicates_resolved"]);                     // ID variant normalized, newer wins
        Assert.Equal(["P001", "Okafor-Lee", "02139"], Q("SELECT patient_number, last_name, zip FROM dental.patient").Single());
        Assert.Equal(1L, Merge(s, "appointment")["duplicates_resolved"]);
        Assert.Equal(1L, Merge(s, "appointment")["orphans"]);                              // its patient was quarantined
        Assert.Equal(1L, Merge(s, "appointment_procedure")["orphans"]);                    // cascades
        Assert.Equal([[(short)1, "D0120", 45.00m], [(short)2, "D1110", 95.00m]],
                     Q("SELECT slot, procedure_code, fee FROM dental.appointment_procedure ORDER BY slot"));
        Assert.Equal([["appointment", 3L, "ORPHAN_FK"], ["appointment_procedure", 3L, "ORPHAN_FK"], ["patient", 3L, "BAD_VALUE"]],
                     Q("SELECT entity, src_rec_id, reason FROM etl.quarantine WHERE dataset = 'dental' ORDER BY 1, 2"));
        Assert.Equal(["PATIENT_TBL", 2L], Q("SELECT _src_table, _src_rec_id FROM dental.patient").Single());   // lineage
    }

    [Fact]
    public void RerunIsANoOp()
    {
        Run(fresh: true);
        var s = Run();
        foreach (var e in new[] { "patient", "appointment", "appointment_procedure" })
            Assert.Equal((0L, 0L), ((long)Merge(s, e)["inserted"]!, (long)Merge(s, e)["updated"]!));
    }

    [Fact]
    public void SourceChangeUpdatesExactlyThatRow()
    {
        Run(fresh: true);
        Sqlite("UPDATE APPT_TBL SET FEE2 = '$99.00' WHERE APPT_NO = '500'");
        var s = Run();
        Assert.Equal(1L, Merge(s, "appointment_procedure")["updated"]);
        Assert.Equal(0L, Merge(s, "appointment")["updated"]);
        Assert.Equal(99.00m, Q("SELECT fee FROM dental.appointment_procedure WHERE slot = 2").Single()[0]);
    }

    [Fact]
    public void DryRunWritesNothingToTarget()
    {
        var s = Run(fresh: true, dryRun: true);
        Assert.Equal(1L, Merge(s, "patient")["inserted"]);                                 // reported...
        Assert.Equal(0L, Q("SELECT count(*) FROM dental.patient").Single()[0]);            // ...but rolled back
        Assert.Equal(0L, Q("SELECT count(*) FROM etl.quarantine WHERE dataset = 'dental'").Single()[0]);
        Assert.Equal([true, "succeeded"], Q($"SELECT dry_run, status FROM etl.migration_run WHERE run_id = {s["run_id"]}").Single());
    }

    [Fact]
    public void ChildSlotDroppedByNewerParentVersionIsNotRevived()
    {
        // Regression for D22: the newer version of appointment 500 has slot 2 removed (no code, zero fee).
        Sqlite("INSERT INTO APPT_TBL VALUES (4, '500', 'P001', '06/01/2020', 'D0120', '$45.00', NULL, '0.00', NULL, '0.00', '', NULL, 'F', '2020-07-01')");
        var s = Run(fresh: true);
        Assert.Equal([[(short)1]], Q("SELECT slot FROM dental.appointment_procedure ORDER BY slot"));
        Assert.Equal(4L, Q("SELECT _src_rec_id FROM dental.appointment").Single()[0]);
        Assert.Equal(1L, Merge(s, "appointment_procedure")["inserted"]);
    }

    [Fact]
    public void RerunDeletesChildrenTheParentNoLongerHas()
    {
        Run(fresh: true);
        Sqlite("UPDATE APPT_TBL SET PROC2 = NULL, FEE2 = '0.00' WHERE APPT_NO = '500'");
        var s = Run();
        Assert.Equal(1L, Merge(s, "appointment_procedure")["deleted"]);
        Assert.Equal([[(short)1]], Q("SELECT slot FROM dental.appointment_procedure"));
        Assert.False(Merge(s, "appointment").ContainsKey("deleted"));                      // an FK alone never deletes
    }

    [Fact]
    public void DeferFksGivesSameResultAndRestoresValidatedConstraints()
    {
        var s = Run(fresh: true, deferFks: true);
        var fkv = (Dictionary<string, object?>)((Dictionary<string, object?>)s["merge"]!)["_fk_validate"]!;
        Assert.Equal(2, fkv["constraints"]);
        Assert.Equal([["dental.appointment", true], ["dental.appointment_procedure", true]],
                     Q("SELECT conrelid::regclass::text, convalidated FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace " +
                       "WHERE n.nspname = 'dental' AND contype = 'f' ORDER BY 1"));
    }

    [Fact]
    public void DeferredFkStillRejectsARealViolation()
    {
        Run(fresh: true);
        var m = new MappingSpec(MappingPath);
        using var ds = Pg.DataSource();
        using var con = ds.OpenConnection();
        using var tx = con.BeginTransaction();
        var fks = Etl.Core.Merge.DropFks(con, tx, m);
        Pg.Exec(con, "INSERT INTO dental.appointment VALUES (999, 'NOPE', NULL, 'T', 1, now())", tx);
        Assert.Throws<PostgresException>(() => Etl.Core.Merge.RestoreFks(con, tx, fks));
        tx.Rollback();
        Assert.Single(Q("SELECT 1 FROM pg_constraint WHERE conrelid = 'dental.appointment'::regclass AND contype = 'f'"));
    }
}
