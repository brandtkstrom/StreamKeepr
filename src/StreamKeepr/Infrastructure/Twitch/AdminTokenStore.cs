using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Persists the admin Twitch token in the private app-data folder, encrypted with
/// Data Protection and replaced atomically with owner-only permissions. Tokens
/// leave the store only through <see cref="GetAsync"/>.
/// </summary>
public sealed class AdminTokenStore(
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpFactory,
    IOptions<StorageOptions> storage,
    IOptions<TwitchOptions> twitch,
    TimeProvider clock,
    ILogger<AdminTokenStore> logger) : IAdminTokenStore, IDisposable
{
    private const string ProtectorPurpose = "StreamKeepr.AdminTokens";

    // Match the app-token provider: refresh five minutes before expiry. A token
    // already inside the window (including an expired one) refreshes immediately.
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);
    private readonly string _tokenPath = AppDataPaths.AdminTokensFile(storage.Value.RecordingsRoot);

    private AdminToken? _token;
    private bool _loaded;
    private TaskCompletionSource _tokenAvailable = CreateSignal();

    public event Action? Changed;

    public async ValueTask<string?> GetAsync(CancellationToken ct)
    {
        string? accessToken = null;
        var cleared = false;

        await _lock.WaitAsync(ct);
        try
        {
            var (token, loadCleared) = await LoadLockedAsync(ct);
            cleared |= loadCleared;

            if (token is not null)
            {
                if (token.ExpiresAt - clock.GetUtcNow() <= RefreshWindow)
                {
                    var refreshed = await RefreshAsync(token, ct);
                    if (refreshed is null)
                    {
                        TryClearLocked();
                        cleared = true;
                    }
                    else
                    {
                        PersistLocked(refreshed);
                        _token = refreshed;
                        accessToken = refreshed.AccessToken;
                        logger.LogInformation("Refreshed the admin Twitch token for {Login}.", refreshed.Login);
                    }
                }
                else
                {
                    accessToken = token.AccessToken;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        if (cleared)
            Changed?.Invoke();

        return accessToken;
    }

    public async Task SaveAsync(AdminToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (string.IsNullOrWhiteSpace(token.Login)
            || string.IsNullOrWhiteSpace(token.AccessToken)
            || string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new ArgumentException(
                "An admin token requires a login, an access token, and a refresh token.",
                nameof(token));
        }

        await _lock.WaitAsync(ct);
        try
        {
            PersistLocked(token);
            _token = token;
            _loaded = true;

            // Waiters share one signal; replace it so a later clear leaves new
            // waiters blocked until the next save.
            var available = _tokenAvailable;
            _tokenAvailable = CreateSignal();
            available.TrySetResult();
        }
        finally
        {
            _lock.Release();
        }

        logger.LogInformation("Saved the admin Twitch token for {Login}.", token.Login);
        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        string? login;

        await _lock.WaitAsync(ct);
        try
        {
            login = _token?.Login;
            DeleteTokenFile();
            _token = null;
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }

        if (login is null)
            logger.LogInformation("Cleared the stored admin Twitch token.");
        else
            logger.LogInformation("Cleared the stored admin Twitch token for {Login}.", login);

        Changed?.Invoke();
    }

    public async Task WaitForTokenAsync(CancellationToken ct)
    {
        Task pending;
        var cleared = false;

        await _lock.WaitAsync(ct);
        try
        {
            var (token, loadCleared) = await LoadLockedAsync(ct);
            cleared = loadCleared;
            if (token is not null)
                return;

            pending = _tokenAvailable.Task;
        }
        finally
        {
            _lock.Release();
        }

        if (cleared)
            Changed?.Invoke();

        await pending.WaitAsync(ct);
    }

    public void Dispose() => _lock.Dispose();

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<(AdminToken? Token, bool Cleared)> LoadLockedAsync(CancellationToken ct)
    {
        if (_loaded)
            return (_token, false);

        try
        {
            if (!File.Exists(_tokenPath))
            {
                _loaded = true;
                return (null, false);
            }

            var encrypted = await File.ReadAllBytesAsync(_tokenPath, ct);
            var plaintext = _protector.Unprotect(encrypted);
            var token = JsonSerializer.Deserialize<AdminToken>(plaintext)
                ?? throw new InvalidDataException("The admin token file is empty.");

            if (string.IsNullOrWhiteSpace(token.Login)
                || string.IsNullOrWhiteSpace(token.AccessToken)
                || string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidDataException("The admin token file is missing required fields.");
            }

            _token = token;
            _loaded = true;
            return (token, false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException
            or UnauthorizedAccessException)
        {
            // A lost key ring or damaged file is not recoverable. Drop the payload
            // so the app falls back to polling and asks the admin to sign in again.
            logger.LogWarning(
                ex,
                "The stored admin Twitch token could not be read; it was cleared. Sign in again.");
            _token = null;
            _loaded = true;
            TryDeleteTokenFile();
            return (null, true);
        }
    }

    private async Task<AdminToken?> RefreshAsync(AdminToken token, CancellationToken ct)
    {
        var options = twitch.Value;
        using var client = httpFactory.CreateClient(TwitchHttpClients.Auth);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
        });

        try
        {
            using var response = await client.PostAsync("oauth2/token", form, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Twitch rejected the admin token refresh for {Login} with HTTP {StatusCode}; " +
                    "the stored token was cleared.",
                    token.Login,
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenRefreshResponse>(ct);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.AccessToken)
                || string.IsNullOrWhiteSpace(payload.RefreshToken)
                || payload.ExpiresIn <= 0)
            {
                logger.LogWarning(
                    "Twitch returned an invalid admin token refresh response for {Login}; " +
                    "the stored token was cleared.",
                    token.Login);
                return null;
            }

            return token with
            {
                AccessToken = payload.AccessToken,
                RefreshToken = payload.RefreshToken,
                ExpiresAt = clock.GetUtcNow().AddSeconds(payload.ExpiresIn),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or NotSupportedException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(
                ex,
                "The admin token refresh failed for {Login}; the stored token was cleared.",
                token.Login);
            return null;
        }
    }

    private void PersistLocked(AdminToken token)
    {
        OwnerOnlyFileSystem.EnsurePrivateDirectory(Path.GetDirectoryName(_tokenPath)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(token);
        OwnerOnlyFileSystem.WriteAtomically(_tokenPath, _protector.Protect(plaintext));
    }

    private void TryClearLocked()
    {
        _token = null;
        _loaded = true;
        TryDeleteTokenFile();
    }

    private void TryDeleteTokenFile()
    {
        try
        {
            DeleteTokenFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The unreadable admin Twitch token file could not be removed.");
        }
    }

    private void DeleteTokenFile()
    {
        try
        {
            File.Delete(_tokenPath);
        }
        catch (DirectoryNotFoundException)
        {
            // No token was ever written: the app-data folder does not exist yet.
        }
    }

    private sealed record TokenRefreshResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
