using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class SettingsSheetUrlTests
{
    [Theory]
    [InlineData("http://localhost/r/abc?settings=open", true)]
    [InlineData("http://localhost/?q=nova&settings=open", true)]
    [InlineData("http://localhost/?settings=OPEN", true)]
    [InlineData("http://localhost/r/abc", false)]
    [InlineData("http://localhost/r/abc?settings=closed", false)]
    [InlineData("http://localhost/r/abc#settings=open", false)]
    // The /settings page shows the sections itself, so the sheet never opens over it.
    [InlineData("http://localhost/settings?settings=open", false)]
    public void IsOpen_reads_the_query_on_any_page_but_settings(string uri, bool expected) =>
        Assert.Equal(expected, SettingsSheetUrl.IsOpen(uri));

    [Theory]
    [InlineData("http://localhost/r/abc", "/r/abc?settings=open")]
    [InlineData("http://localhost/?q=nova&sort=az", "/?q=nova&sort=az&settings=open")]
    [InlineData("http://localhost/?settings=open&q=nova", "/?q=nova&settings=open")]
    [InlineData("http://localhost/r/abc#chapters", "/r/abc?settings=open")]
    public void Open_adds_the_key_once_and_keeps_the_other_keys(string uri, string expected) =>
        Assert.Equal(expected, SettingsSheetUrl.Open(uri));

    [Theory]
    [InlineData("http://localhost/r/abc?settings=open", "/r/abc")]
    [InlineData("http://localhost/?q=nova&settings=open&sort=az", "/?q=nova&sort=az")]
    [InlineData("http://localhost/?q=nova", "/?q=nova")]
    public void Close_removes_only_the_key(string uri, string expected) =>
        Assert.Equal(expected, SettingsSheetUrl.Close(uri));
}
