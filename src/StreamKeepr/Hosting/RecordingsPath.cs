using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Hosting;

/// <summary>
/// Resolves relative media path segments under the recordings root. Both the
/// <c>/media</c> allowlist and the live index endpoint use this one containment
/// rule, so a traversal fix cannot land in only one place.
/// </summary>
internal static class RecordingsPath
{
    public static bool TryResolve(
        string recordingsRoot, IReadOnlyList<string> segments, out string candidate)
    {
        candidate = "";

        try
        {
            string root = Path.GetFullPath(recordingsRoot);
            string prefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(Path.Combine([root, .. segments]));

            if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            candidate = resolved;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a session folder from a public channel and session id, rejecting any segment
    /// that is not a valid Twitch login or session id before touching the file system.
    /// </summary>
    public static bool TryResolveSession(
        string recordingsRoot, string channel, string id, out string directory)
    {
        directory = "";
        return AuthOptions.IsValidLogin(channel)
            && Session.IsValidId(id)
            && TryResolve(recordingsRoot, [channel, id], out directory);
    }
}
