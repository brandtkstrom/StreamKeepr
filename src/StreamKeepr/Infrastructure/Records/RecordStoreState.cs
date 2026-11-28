namespace StreamKeepr.Infrastructure.Records;

/// <summary>What <see cref="RecordStoreStartup"/> found and did during this process start.</summary>
public sealed class RecordStoreState
{
    /// <summary>True once the database is open, checked, and fully migrated.</summary>
    public bool Prepared { get; private set; }

    internal void MarkPrepared() => Prepared = true;

    /// <summary>Fails fast when a store is used before the startup step migrated the database.</summary>
    public void EnsurePrepared()
    {
        if (!Prepared)
        {
            throw new InvalidOperationException(
                "The record store has not been prepared; RecordStoreStartup must run before any store loads.");
        }
    }
}

/// <summary>The record store cannot be used; startup stops and the database is left as it was.</summary>
public sealed class RecordStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);
