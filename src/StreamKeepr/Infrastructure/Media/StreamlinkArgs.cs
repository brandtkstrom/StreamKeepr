using System.Globalization;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>Builds the Streamlink command line that writes the source stream to stdout.</summary>
public static class StreamlinkArgs
{
    public static IReadOnlyList<string> For(string channel, RecorderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(options);

        List<string> args =
        [
            "--stdout", "--loglevel", "info",
            "--hls-live-edge", options.HlsLiveEdge.ToString(CultureInfo.InvariantCulture),
            "--stream-segment-threads", "2",
            "--stream-segment-timeout", "20",
            "--stream-timeout", "60",
            "--retry-open", "3",
            "--twitch-supported-codecs", "h264,h265,av1",
        ];

        if (!string.IsNullOrWhiteSpace(options.TwitchUserOAuthToken))
        {
            args.AddRange(
            [
                "--twitch-api-header",
                $"Authorization=OAuth {options.TwitchUserOAuthToken}",
            ]);
        }

        args.AddRange([$"https://www.twitch.tv/{channel}", options.Quality]);
        return args;
    }
}
