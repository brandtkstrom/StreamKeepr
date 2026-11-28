using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace StreamKeepr.Infrastructure.Records;

/// <summary>What counts as a failure of the record store itself (ADR-0014's write-failure policy).</summary>
public static class RecordStoreFailures
{
    /// <summary>
    /// True for a database or save failure. Callers that log and continue on a store failure
    /// catch only these, so programming errors still surface.
    /// </summary>
    public static bool IsStoreFailure(Exception ex) => ex is DbException or DbUpdateException;
}
