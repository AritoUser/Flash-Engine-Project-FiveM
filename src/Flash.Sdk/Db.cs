using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Database API -- parameterized SQL (@p0, @p1, ...) against a configurable provider.
//
//  PROVIDERS (BYO-DB):
//    - "sqlite" (default): zero-setup, file-based (Microsoft.Data.Sqlite). Perfect for
//      development/small servers -- works without any config.
//    - "mysql": MySQL/MariaDB via MySqlConnector (MIT, truly async). What real
//      production FiveM servers run.
//    Both go through System.Data.Common (DbConnection/DbCommand) -> ONE code path,
//    no provider-specific branches in the query logic.
//
//  CONFIG (resolution order):
//    1. Db.Configure(provider, connectionString) in code (wins, e.g. for tests).
//    2. Convars in server.cfg, read once on first use:
//         set flash_db_provider   "mysql"
//         set flash_db_connection "Server=127.0.0.1;Database=flash;User=...;Password=..."
//       (`set`, NOT `sets` -- `sets` publishes the value to the public server list!)
//    3. Default: SQLite file "flash-engine.db" in the server data directory.
//
//  THREADING:
//    The sync API (Execute/Query/Scalar/Insert) blocks the script thread -- fine for
//    OnStart/DDL and SQLite, NOT for per-frame or per-player hot paths on MySQL.
//    The async API (ExecuteAsync/...) runs the database work on the thread pool and
//    resumes the caller on the script thread (via the resource's FlashSyncContext,
//    same mechanism as Async.Delay/Http). Task.Run is deliberate even though ADO has
//    async calls: Microsoft.Data.Sqlite's "async" methods are synchronous under the
//    hood and would otherwise block the script thread anyway.
// =====================================================================================

/// <summary>
/// Simple database API for resources. Parameterized queries (@p0, @p1, ...) against a
/// configurable provider: SQLite (zero-setup default) or MySQL/MariaDB.
/// Configure via convars in server.cfg (flash_db_provider / flash_db_connection) or
/// <see cref="Configure(string, string)"/>. Use the ...Async variants in hot paths --
/// they don't block the server frame and resume on the script thread.
/// </summary>
public static class Db
{
    private const string DefaultSqliteConnection = "Data Source=flash-engine.db";

    // The active provider. Written only on the script thread (Configure/first use);
    // async operations capture the reference BEFORE hopping to the thread pool.
    private static DbProvider? _provider;

    /// <summary>Name of the active provider: "sqlite" or "mysql". Lets resources pick
    /// dialect-specific SQL (e.g. AUTOINCREMENT vs AUTO_INCREMENT) where needed.</summary>
    public static string Provider
    {
        get
        {
            // If not yet resolved, the getter would read convars (natives) -- which is a
            // hard crash off the script thread. The async methods assert the script
            // thread before resolving; the public getter must do the same. Once resolved
            // (normal case after OnStart), this is just a field read and thread-safe. (#94)
            if (_provider == null && SynchronizationContext.Current is not FlashSyncContext)
                throw new InvalidOperationException(
                    "Read Db.Provider first on the script thread (e.g. in OnStart or after a query); " +
                    "the initial read resolves the provider via convars, which cannot run off-thread.");
            return Resolve().Name;
        }
    }

    /// <summary>Sets a SQLite connection string (back-compat overload).</summary>
    public static void Configure(string connectionString)
        => Configure("sqlite", connectionString);

    /// <summary>
    /// Selects the provider ("sqlite" or "mysql") + connection string in code. Optional --
    /// without a call, the convars flash_db_provider/flash_db_connection decide (or the
    /// SQLite default). Call in OnStart, before the first query.
    /// </summary>
    public static void Configure(string provider, string connectionString)
        => _provider = Create(provider, connectionString);

    // === Sync API (script thread; fine for OnStart/DDL -- use async in hot paths) ====

    /// <summary>Command without a result set (INSERT/UPDATE/DELETE/DDL). Returns affected rows.</summary>
    public static int Execute(string sql, params object?[] args)
    {
        var p = Resolve();
        using var con = p.Open();
        using var cmd = Prepare(con, sql, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Single value (first column of the first row), or null.</summary>
    public static object? Scalar(string sql, params object?[] args)
    {
        var p = Resolve();
        using var con = p.Open();
        using var cmd = Prepare(con, sql, args);
        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Multiple rows as a list of column-name → value maps.</summary>
    public static List<Dictionary<string, object?>> Query(string sql, params object?[] args)
    {
        var p = Resolve();
        using var con = p.Open();
        using var cmd = Prepare(con, sql, args);
        using var reader = cmd.ExecuteReader();
        return ReadAll(reader);
    }

    /// <summary>
    /// INSERT that returns the generated id (AUTOINCREMENT/AUTO_INCREMENT key) of the
    /// new row. Provider-portable: reads the id on the SAME connection right after the
    /// insert (last_insert_rowid()/LAST_INSERT_ID(); `INSERT ... RETURNING` would not
    /// work on MySQL 8, and a separate Scalar call would use a different connection).
    /// </summary>
    public static long Insert(string sql, params object?[] args)
    {
        var p = Resolve();
        using var con = p.Open();
        using (var cmd = Prepare(con, sql, args))
            cmd.ExecuteNonQuery();
        using var idCmd = Prepare(con, p.LastInsertIdSql, Array.Empty<object?>());
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    // === Async API (thread pool; the await resumes on the script thread) ============

    /// <summary>Like <see cref="Execute"/>, but without blocking the server frame.</summary>
    public static Task<int> ExecuteAsync(string sql, params object?[] args)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using var cmd = Prepare(con, sql, args);
            return await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>Like <see cref="Scalar"/>, but without blocking the server frame.</summary>
    public static Task<object?> ScalarAsync(string sql, params object?[] args)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using var cmd = Prepare(con, sql, args);
            object? result = await cmd.ExecuteScalarAsync();
            return result is DBNull ? null : result;
        });
    }

    /// <summary>Like <see cref="Query"/>, but without blocking the server frame.</summary>
    public static Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params object?[] args)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using var cmd = Prepare(con, sql, args);
            await using var reader = await cmd.ExecuteReaderAsync();
            return ReadAll(reader);
        });
    }

    // === Typed mapping (Dapper-like): rows -> strongly-typed objects (#115) =============
    //
    //  Columns map to public writable properties by name, case-insensitively and ignoring
    //  underscores -- so `vip_level` binds to `VipLevel`, `created_at` to `CreatedAt`. Values
    //  are coerced through Args.ToType (the SAME numeric-safe path as event/state args), so
    //  MySQL `bigint`/`double`/`decimal` land in `int`/`float`/... without cast exceptions
    //  (the #85 class of bugs). NULL columns leave the property at its default.
    //  The per-type column->setter map is built once via reflection and cached (see Mapper<T>).

    /// <summary>Runs a query and maps each row to a new <typeparamref name="T"/> (blocks the frame; use <see cref="QueryAsync{T}"/> in hot paths).</summary>
    public static List<T> Query<T>(string sql, params object?[] args) where T : new()
        => Mapper<T>.Map(Query(sql, args));

    /// <summary>Like <see cref="Query{T}"/>, but without blocking the server frame; the await resumes on the script thread.</summary>
    public static async Task<List<T>> QueryAsync<T>(string sql, params object?[] args) where T : new()
        => Mapper<T>.Map(await QueryAsync(sql, args));

    /// <summary>Maps the FIRST row to <typeparamref name="T"/>, or null if the query returned no rows.</summary>
    public static async Task<T?> QuerySingleAsync<T>(string sql, params object?[] args) where T : class, new()
    {
        var rows = await QueryAsync(sql, args);
        return rows.Count == 0 ? null : Mapper<T>.MapRow(rows[0]);
    }

    /// <summary>
    /// Executes several commands in ONE transaction (all or nothing) without blocking
    /// the server frame. For multi-row invariants — e.g. a money transfer (debit +
    /// credit + audit row): a crash between the statements must not create or destroy
    /// money. Returns the total number of affected rows; on any error everything is
    /// rolled back and the exception propagates to the caller's await.
    /// </summary>
    public static Task<int> ExecuteBatchAsync(params (string Sql, object?[] Args)[] commands)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using var tx = await con.BeginTransactionAsync();
            try
            {
                int affected = 0;
                foreach (var (sql, args) in commands)
                {
                    await using var cmd = Prepare(con, sql, args);
                    cmd.Transaction = tx;
                    affected += await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return affected;
            }
            catch
            {
                await tx.RollbackAsync(); // explizit statt implizit via Dispose -> klare Semantik
                throw;
            }
        });
    }

    /// <summary>
    /// Like <see cref="ExecuteBatchAsync"/>, but each command may assert how many rows it
    /// MUST affect: <c>RequiredAffected &gt;= 0</c> → the statement must affect exactly that
    /// many rows; <c>&lt; 0</c> → no assertion. If a guarded statement affects a different
    /// count, the WHOLE transaction is rolled back and the method returns <c>false</c> — a
    /// CLEAN rejection, not an error (e.g. an "insufficient funds" UPDATE whose WHERE guard
    /// matched no row). Returns <c>true</c> when everything committed. A real DB error still
    /// rolls back and propagates to the caller's await.
    ///
    /// This makes CONDITIONAL multi-row invariants crash-atomic: the guard (a WHERE clause)
    /// and every dependent write live in one transaction, so a guard that rejects can never
    /// half-apply the batch (the classic "debit the player but the second leg no-ops" money
    /// bug). Use it when a batch mixes guarded UPDATEs with dependent writes.
    /// </summary>
    public static Task<bool> ExecuteGuardedBatchAsync(params (string Sql, object?[] Args, int RequiredAffected)[] commands)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using var tx = await con.BeginTransactionAsync();
            try
            {
                foreach (var (sql, args, required) in commands)
                {
                    await using var cmd = Prepare(con, sql, args);
                    cmd.Transaction = tx;
                    int affected = await cmd.ExecuteNonQueryAsync();
                    if (required >= 0 && affected != required)
                    {
                        // Guard not satisfied (e.g. a coverage WHERE matched 0 rows) -> discard
                        // the ENTIRE transaction: no partial apply. Clean rejection.
                        await tx.RollbackAsync();
                        return false;
                    }
                }
                await tx.CommitAsync();
                return true;
            }
            catch
            {
                await tx.RollbackAsync(); // real DB error -> roll back + rethrow
                throw;
            }
        });
    }

    /// <summary>Like <see cref="Insert"/>, but without blocking the server frame.</summary>
    public static Task<long> InsertAsync(string sql, params object?[] args)
    {
        var p = ResolveForAsync();
        return Task.Run(async () =>
        {
            await using var con = await p.OpenAsync();
            await using (var cmd = Prepare(con, sql, args))
                await cmd.ExecuteNonQueryAsync();
            await using var idCmd = Prepare(con, p.LastInsertIdSql, Array.Empty<object?>());
            return Convert.ToInt64(await idCmd.ExecuteScalarAsync());
        });
    }

    // === Internals ===================================================================

    // Resolve the active provider; on first use WITHOUT explicit Configure, read the
    // convars. Runs on the script thread (all public entry points come through here
    // before any thread-pool hop) -- natives are safe.
    private static DbProvider Resolve()
    {
        if (_provider != null) return _provider;
        string provider = global::Flash.Natives.Cfx.GetConvar("flash_db_provider", "sqlite") ?? "sqlite";
        string conn = global::Flash.Natives.Cfx.GetConvar("flash_db_connection", "") ?? "";
        if (conn.Length == 0)
        {
            if (!provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"flash_db_provider is '{provider}' but flash_db_connection is not set. " +
                    "Add e.g.:  set flash_db_connection \"Server=127.0.0.1;Database=flash;User=...;Password=...\"");
            conn = DefaultSqliteConnection;
        }
        _provider = Create(provider, conn);
        Log.Info($"Db: provider '{_provider.Name}' active.");
        return _provider;
    }

    // Async entry points additionally require a resource context (like Async.Delay):
    // without a FlashSyncContext the continuation would resume on a thread-pool thread,
    // and native/state access after the await would crash the server. A clear error
    // beats that silent off-thread bug.
    private static DbProvider ResolveForAsync()
    {
        if (SynchronizationContext.Current is not FlashSyncContext)
            throw new InvalidOperationException(
                "Call the Db...Async methods only inside a Flash resource (OnStart/OnTick/handlers).");
        return Resolve();
    }

    private static DbProvider Create(string provider, string connectionString)
        => provider.ToLowerInvariant() switch
        {
            "sqlite" => new SqliteProvider(connectionString),
            "mysql" or "mariadb" => new MySqlProvider(connectionString),
            _ => throw new ArgumentException(
                $"Unknown DB provider '{provider}' (supported: sqlite, mysql).", nameof(provider)),
        };

    // Build a command with positional arguments bound as @p0, @p1, ...
    // (that's how the dev writes them in SQL).
    private static DbCommand Prepare(DbConnection con, string sql, object?[] args)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static List<Dictionary<string, object?>> ReadAll(DbDataReader reader)
    {
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    // ---- Per-type row mapper for Query<T>/QueryAsync<T>. Built once (reflection) and
    //      cached: column names -> property setters. Reflection SetValue is used rather
    //      than compiled expression trees deliberately -- DB result mapping is not a
    //      per-frame hot path, and the cached lookup keeps it allocation-light and simple. ----
    private static class Mapper<T> where T : new()
    {
        // Key = normalized column/property name (lower-case, underscores stripped) so
        // `vip_level` and `VipLevel` collide onto the same setter.
        private static readonly Dictionary<string, Action<T, object?>> s_setters = Build();

        private static Dictionary<string, Action<T, object?>> Build()
        {
            var map = new Dictionary<string, Action<T, object?>>();
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                var type = prop.PropertyType;
                // A non-nullable value-type property can't hold null (DB NULL / failed coercion):
                // leave it at its default. A reference type or Nullable<T> CAN, so a DB NULL must
                // overwrite a C# default initializer (reflect the DB state, like Dapper). (#166)
                bool canBeNull = !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
                map[Normalize(prop.Name)] = (target, value) =>
                {
                    object? converted = Args.ToType(value, type);
                    if (converted != null || canBeNull) prop.SetValue(target, converted);
                };
            }
            return map;
        }

        public static List<T> Map(List<Dictionary<string, object?>> rows)
        {
            var list = new List<T>(rows.Count);
            foreach (var row in rows) list.Add(MapRow(row));
            return list;
        }

        public static T MapRow(Dictionary<string, object?> row)
        {
            var obj = new T();
            // Do NOT skip NULL columns here: a DB NULL must reach the setter so it can null out
            // a reference/nullable property (overwriting any C# default initializer). The setter
            // itself decides whether null is assignable to the target type. (#166)
            foreach (var (column, value) in row)
                if (s_setters.TryGetValue(Normalize(column), out var set)) set(obj, value);
            return obj;
        }

        private static string Normalize(string name)
        {
            Span<char> buf = stackalloc char[name.Length];
            int n = 0;
            foreach (char c in name)
                if (c != '_') buf[n++] = char.ToLowerInvariant(c);
            return new string(buf[..n]);
        }
    }

    // ---- Provider abstraction: connection factory + the few dialect differences. ----
    private abstract class DbProvider
    {
        public abstract string Name { get; }
        /// <summary>Dialect SQL that returns the auto-increment id of the last INSERT
        /// on the same connection.</summary>
        public abstract string LastInsertIdSql { get; }
        protected abstract DbConnection CreateConnection();

        public DbConnection Open()
        {
            var con = CreateConnection();
            con.Open();
            return con;
        }

        public async Task<DbConnection> OpenAsync()
        {
            var con = CreateConnection();
            await con.OpenAsync();
            return con;
        }
    }

    private sealed class SqliteProvider : DbProvider
    {
        private readonly string _connectionString;
        public SqliteProvider(string connectionString) => _connectionString = connectionString;
        public override string Name => "sqlite";
        public override string LastInsertIdSql => "SELECT last_insert_rowid()";
        protected override DbConnection CreateConnection()
            => new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
    }

    private sealed class MySqlProvider : DbProvider
    {
        private readonly string _connectionString;
        public MySqlProvider(string connectionString) => _connectionString = connectionString;
        public override string Name => "mysql";
        public override string LastInsertIdSql => "SELECT LAST_INSERT_ID()";
        protected override DbConnection CreateConnection()
            => new MySqlConnector.MySqlConnection(_connectionString);
    }
}
