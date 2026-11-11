using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class StreamlinkArgsTests
{
    [Fact]
    public void For_matches_the_recorder_contract_without_a_token()
    {
        var options = new RecorderOptions
        {
            HlsLiveEdge = 6,
            Quality = "best",
        };

        IReadOnlyList<string> args = StreamlinkArgs.For("streamer", options);

        string[] expected =
        [
            "--stdout", "--loglevel", "info",
            "--hls-live-edge", "6",
            "--stream-segment-threads", "2",
            "--stream-segment-timeout", "20",
            "--stream-timeout", "60",
            "--retry-open", "3",
            "--twitch-supported-codecs", "h264,h265,av1",
            "https://www.twitch.tv/streamer", "best",
        ];

        Assert.Equal(expected, args);
    }

    [Fact]
    public void For_adds_the_api_header_before_the_url_when_a_token_is_configured()
    {
        var options = new RecorderOptions
        {
            HlsLiveEdge = 4,
            Quality = "720p60",
            TwitchUserOAuthToken = "secret-token",
        };

        IReadOnlyList<string> args = StreamlinkArgs.For("streamer", options);

        List<string> list = args.ToList();
        int header = list.IndexOf("--twitch-api-header");
        Assert.True(header >= 0);
        Assert.Equal("Authorization=OAuth secret-token", list[header + 1]);
        Assert.Equal("https://www.twitch.tv/streamer", list[header + 2]);
        Assert.Equal("720p60", list[header + 3]);
        Assert.Equal("4", list[list.IndexOf("--hls-live-edge") + 1]);
    }

    [Fact]
    public void For_always_requests_the_supported_codec_set()
    {
        IReadOnlyList<string> args = StreamlinkArgs.For(
            "streamer",
            new RecorderOptions { TwitchUserOAuthToken = "secret-token" });

        int codecs = args.ToList().IndexOf("--twitch-supported-codecs");
        Assert.True(codecs >= 0);
        Assert.Equal("h264,h265,av1", args[codecs + 1]);
    }
}
