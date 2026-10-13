using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Etl.Core.Load;

/// <summary>Postgres connection settings and helpers. Override with ETL_DSN (postgresql://user:pass@host:port/db).</summary>
public static class Pg
{
    public const string DefaultDsn = "postgresql://etl:etl@localhost:5433/target";

    /// <summary>Npgsql takes key=value connection strings, not URLs; translate the shared ETL_DSN format.</summary>
    public static string ConnectionString(string? dsn = null)
    {
        var uri = new Uri(dsn ?? Environment.GetEnvironmentVariable("ETL_DSN") ?? DefaultDsn);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            MaxPoolSize = 64,
            CommandTimeout = 0,   // migrations run long statements; the run itself is the timeout boundary
        }.ToString();
    }

    public static NpgsqlDataSource DataSource(string? dsn = null) => NpgsqlDataSource.Create(ConnectionString(dsn));

    /// <summary>Quote an identifier. Identifiers can't be bound as parameters, so they come only from the compiled
    /// mapping (an allowlist) and are always quoted; values are always parameters.</summary>
    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public static string Q(string schema, string table) => $"{Q(schema)}.{Q(table)}";

    public static int Exec(NpgsqlConnection con, string sql, NpgsqlTransaction? tx = null, params object?[] args)
    {
        using var cmd = new NpgsqlCommand(sql, con, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(NpgsqlConnection con, string sql, NpgsqlTransaction? tx = null, params object?[] args)
    {
        using var cmd = new NpgsqlCommand(sql, con, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A target column's Postgres type and how to coerce an engine value to it. Binary COPY needs the exact wire type
/// (a long into a smallint column fails), so every staging column gets a typed coercion built from the catalog.
/// </summary>
public sealed record PgColumn(string Name, string UdtName)
{
    public NpgsqlDbType DbType => UdtName switch
    {
        "text" => NpgsqlDbType.Text,
        "varchar" => NpgsqlDbType.Varchar,
        "bpchar" => NpgsqlDbType.Char,
        "int2" => NpgsqlDbType.Smallint,
        "int4" => NpgsqlDbType.Integer,
        "int8" => NpgsqlDbType.Bigint,
        "numeric" => NpgsqlDbType.Numeric,
        "bool" => NpgsqlDbType.Boolean,
        "date" => NpgsqlDbType.Date,
        "timestamp" => NpgsqlDbType.Timestamp,
        "jsonb" => NpgsqlDbType.Jsonb,
        _ => throw new NotSupportedException($"column {Name}: unsupported type {UdtName}"),
    };

    public object Coerce(object v) => (UdtName, v) switch
    {
        ("int2", long l) => checked((short)l),
        ("int4", long l) => checked((int)l),
        ("int8", long l) => l,
        ("numeric", long l) => (decimal)l,
        ("timestamp", string s) => DateTime.ParseExact(s, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss"],
                                                       CultureInfo.InvariantCulture, DateTimeStyles.None),
        ("text" or "varchar" or "bpchar", long l) => l.ToString(CultureInfo.InvariantCulture),
        _ => v,
    };

    /// <summary>Catalog types for a table's columns, in the given order.</summary>
    public static IReadOnlyList<PgColumn> For(NpgsqlConnection con, string schema, string table, IReadOnlyList<string> columns)
    {
        var types = new Dictionary<string, string>();
        using (var cmd = new NpgsqlCommand(
            "SELECT column_name, udt_name FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2", con))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = schema });
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            using var r = cmd.ExecuteReader();
            while (r.Read()) types[r.GetString(0)] = r.GetString(1);
        }
        return columns.Select(c => new PgColumn(c, types.TryGetValue(c, out var t) ? t
            : throw new InvalidOperationException($"{schema}.{table} has no column {c}"))).ToList();
    }
}
