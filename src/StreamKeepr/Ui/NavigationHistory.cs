namespace StreamKeepr.Ui;

/// <summary>
/// Whether this circuit has shown an earlier StreamKeepr page, so "Exit settings" can go back
/// through browser history instead of leaving the app. Pure: the shell feeds it each location.
/// </summary>
/// <remarks>
/// Only a change of path counts as a page: a replaced query (the <c>authError</c> notifier) or
/// an in-page anchor adds nothing to go back to. Browser back and forward raise the same
/// location event as a link, so a viewer who opened Settings directly, went Home, then pressed
/// the browser's Back would leave the app on exit; that is an accepted limitation.
/// </remarks>
public sealed class NavigationHistory
{
    private string _path;

    /// <param name="startUri">The absolute URI the circuit started on.</param>
    public NavigationHistory(string startUri) => _path = PathOf(startUri);

    /// <summary>True once the circuit has moved from one page to another.</summary>
    public bool HasPreviousPage { get; private set; }

    /// <summary>Records a location change to the absolute <paramref name="uri"/>.</summary>
    public void Visit(string uri)
    {
        string path = PathOf(uri);
        if (!string.Equals(path, _path, StringComparison.Ordinal))
            HasPreviousPage = true;
        _path = path;
    }

    private static string PathOf(string uri) => new Uri(uri, UriKind.Absolute).AbsolutePath;
}
