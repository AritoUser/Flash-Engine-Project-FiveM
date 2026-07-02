using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Flash;

/// <summary>
/// Simple database API for resources. Parameterized queries (@p0, @p1, ...) against a
/// configurable connection string.
///
/// FIRST ADAPTER: SQLite (zero-setup, file-based — default "Data Source=flash-engine.db").
/// The API is deliberately slim/DB-agnostic (Execute/Query/Scalar); further providers
/// (MySQL/Postgres) will use the same API surface + config (vision: BYO-DB).
/// </summary>
public static class Db
{
    private static string _connectionString = "Data Source=flash-engine.db";

    /// <summary>Sets the connection string (e.g. from server config). Default = local
    /// SQLite file in the server directory.</summary>
    public static void Configure(string connectionString) => _connectionString = connectionString;

    /// <summary>Command without a result set (INSERT/UPDATE/DELETE/DDL). Returns affected rows.</summary>
    public static int Execute(string sql, params object?[] args)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Single value (first column of the first row), or null.</summary>
    public static object? Scalar(string sql, params object?[] args)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Multiple rows as a list of column-name → value maps.</summary>
    public static List<Dictionary<string, object?>> Query(string sql, params object?[] args)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    // Bind positional arguments as @p0, @p1, ... (that's how the dev writes them in SQL).
    private static void Bind(SqliteCommand cmd, object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
