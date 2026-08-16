using Microsoft.EntityFrameworkCore;
using Praxy.Persistence;

namespace Praxy.Tables;

/// <summary>
/// Runs a metadata write plus raw DDL in one Postgres transaction — the "sync DDL" half of the
/// engine (architecture.md §4.5). Postgres' transactional DDL means a rolled-back metadata insert
/// also rolls back the <c>CREATE TABLE</c>/<c>ALTER TABLE</c>, so there is no window where the two
/// can disagree.
/// </summary>
public static class SchemaDdl
{
    public static async Task<T> InTransactionAsync<T>(
        PraxyDb db, Func<Task<T>> body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static Task InTransactionAsync(PraxyDb db, Func<Task> body, CancellationToken ct) =>
        InTransactionAsync(db, async () => { await body(); return true; }, ct);

    /// <summary>Executes a fully server-generated DDL statement — never build this string from request input.</summary>
    public static Task ExecuteAsync(PraxyDb db, string sql, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(sql, ct);

    /// <summary><c>lock_timeout</c> guard so a blocked ALTER never queues every later query against the table.</summary>
    public static Task SetLockTimeoutAsync(PraxyDb db, TimeSpan timeout, CancellationToken ct) =>
        ExecuteAsync(db, $"SET LOCAL lock_timeout = '{(int)timeout.TotalMilliseconds}ms'", ct);
}
