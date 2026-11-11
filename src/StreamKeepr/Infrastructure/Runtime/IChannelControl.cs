namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>The authenticated channel-control surface. Recording continues while it is busy.</summary>
public interface IChannelControl
{
    Task AddChannelAsync(string login, CancellationToken ct);

    /// <summary>Stops and finalizes any open capture; recordings stay in the library.</summary>
    Task RemoveChannelAsync(string login, CancellationToken ct);

    Task StopRecordingAsync(string login, CancellationToken ct);

    Task ResumeRecordingAsync(string login, CancellationToken ct);
}

/// <summary>The login is valid but Twitch could not resolve it.</summary>
public sealed class UnknownChannelException(string login)
    : InvalidOperationException($"Twitch channel '{login}' was not found.")
{
    public string Login { get; } = login;
}
