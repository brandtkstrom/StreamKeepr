namespace StreamKeepr.Tests.Records;

/// <summary>What a deletion leaves on disk, for tests that check it is really gone.</summary>
internal static class RecordStoreFiles
{
    /// <summary>The raw bytes of the database and its write-ahead log, as Latin-1 text to search.</summary>
    public static string Text(string databasePath)
    {
        var text = new System.Text.StringBuilder();
        foreach (string path in new[] { databasePath, databasePath + "-wal" })
        {
            if (File.Exists(path))
                text.Append(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        }

        return text.ToString();
    }
}
