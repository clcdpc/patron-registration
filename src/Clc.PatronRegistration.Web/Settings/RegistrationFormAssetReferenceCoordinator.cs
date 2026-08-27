using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clc.PatronRegistration.Web.Settings;

/// <summary>
/// Serializes the short operations that can establish or remove a registration
/// form image reference with orphan cleanup. The row is deliberately durable:
/// every application instance uses the same database lock rather than an
/// in-process mutex.
/// </summary>
internal static class RegistrationFormAssetReferenceCoordinator
{
    private const string LockSql = """
        select LockId
        from dbo.RegistrationFormAssetReferenceLocks with (updlock,holdlock)
        where LockId = 1;
        """;

    // These hooks are test-only seams for deterministic SQL-backed interleaving
    // tests. They are null in production and do not change the locking protocol.
    internal static Action<string>? BeforeAcquireForTesting { get; set; }
    internal static Action<string>? AfterAcquireForTesting { get; set; }

    /// <summary>
    /// Lock order: this gate is acquired before any draft, preview-link, scope
    /// version, setting, draft-change, or asset row locks. Cleanup acquires the
    /// same gate before reading references or deleting assets. Keeping the gate
    /// first prevents an asset/reference lock inversion between saves, draft
    /// lifecycle operations, form-code deletion, and cleanup. Preview-link-only
    /// lifecycle operations do not need the gate: they neither establish nor
    /// remove an asset reference, and cleanup never locks preview-link rows.
    /// </summary>
    internal static void Acquire(SqlConnection connection, IDbTransaction transaction, string operation)
    {
        BeforeAcquireForTesting?.Invoke(operation);

        var lockId = connection.QuerySingleOrDefault<int?>(LockSql, transaction: transaction);
        if (lockId != 1)
        {
            throw new InvalidOperationException(
                "The registration-form asset reference lock row is missing. Apply the database convergence update before saving settings.");
        }

        AfterAcquireForTesting?.Invoke(operation);
    }
}
