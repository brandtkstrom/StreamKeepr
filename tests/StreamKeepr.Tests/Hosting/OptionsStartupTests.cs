using Microsoft.Extensions.Options;

namespace StreamKeepr.Tests.Hosting;

public sealed class OptionsStartupTests
{
    [Theory]
    [InlineData("Twitch:ClientId", "", "ClientId")]
    [InlineData("Recorder:FfprobePath", "", "FfprobePath")]
    [InlineData("Recorder:SegmentSeconds", "1", "SegmentSeconds")]
    [InlineData("Recorder:FinalizeTimeoutSeconds", "16", "FinalizeTimeoutSeconds")]
    [InlineData("Recorder:HealthyPartSeconds", "1", "HealthyPartSeconds")]
    [InlineData("Twitch:Channels:0", "not a login", "Channels")]
    [InlineData("Storage:RecordingsRoot", "", "RecordingsRoot")]
    [InlineData("Auth:AllowedTwitchLogins:0", "not a login", "AllowedTwitchLogins")]
    [InlineData("Hosting:ForwardedHeaders:KnownProxies:0", "not-an-ip", "KnownProxies")]
    [InlineData("Hosting:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99", "KnownNetworks")]
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
