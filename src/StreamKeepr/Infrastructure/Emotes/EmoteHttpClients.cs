namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>Names of the HTTP clients used by the emote providers.</summary>
internal static class EmoteHttpClients
{
    public const string Bttv = "emotes-bttv";
    public const string BttvBaseAddress = "https://api.betterttv.net/";

    public const string SevenTv = "emotes-7tv";
    public const string SevenTvBaseAddress = "https://api.7tv.app/";
}
