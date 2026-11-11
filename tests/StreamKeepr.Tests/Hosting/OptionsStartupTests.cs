using Microsoft.Extensions.Options;

namespace StreamKeepr.Tests.Hosting;

public sealed class OptionsStartupTests
{
    [Theory]
    [InlineData("Twitch:ClientId", "", "ClientId")]
    [InlineData("Recorder:SegmentSeconds", "1", "SegmentSeconds")]
    [InlineData("Storage:RecordingsRoot", "", "RecordingsRoot")]
    [InlineData("Auth:AllowedTwitchLogins:0", "not a login", "AllowedTwitchLogins")]
    public void Invalid_options_stop_startup_with_the_offending_setting(
        string key, string value, string expectedSetting)
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [key] = value,
        });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(error.Failures,
            failure => failure.Contains(expectedSetting, StringComparison.Ordinal));
    }
}
