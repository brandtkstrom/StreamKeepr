using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Persists the admin Twitch token as the record store's single <see cref="AdminTokenRow"/>
/// (ADR-0014): Data Protection ciphertext only, never plaintext, written in one transaction.
/// Tokens leave the store only through <see cref="GetAsync"/> and <see cref="RefreshAsync"/>.
/// </summary>
/// <remarks>
/// A row that cannot be decrypted or parsed (a lost key ring, or damage) is deleted and the admin
/// signs in again. A record store that cannot be reached is not treated as unreadable: the error
/// propagates and nothing is cleared, so the next call retries.
/// </remarks>
public sealed class AdminTokenStore(
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpFactory,
    IDbContextFactory<RecordStoreDbContext> records,
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
                    (string? refreshedAccess, bool refreshCleared) =
                        await RefreshAndPersistLockedAsync(token, ct);
                    accessToken = refreshedAccess;
                    cleared |= refreshCleared;
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

    public async ValueTask<string?> RefreshAsync(CancellationToken ct)
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
                (string? refreshedAccess, bool refreshCleared) =
                    await RefreshAndPersistLockedAsync(token, ct);
                accessToken = refreshedAccess;
                cleared |= refreshCleared;
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
            await PersistLockedAsync(token, ct);
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
            await DeleteRowAsync(ct);
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

        // Read errors propagate with _loaded still false, so the next call reads again.
        string? ciphertext;
        await using (RecordStoreDbContext db = await records.CreateDbContextAsync(ct))
        {
            ciphertext = await db.AdminTokens
                .AsNoTracking()
                .Where(row => row.Id == AdminTokenRow.SingletonId)
                .Select(row => row.Ciphertext)
                .SingleOrDefaultAsync(ct);
        }

        if (ciphertext is null)
        {
            _loaded = true;
            return (null, false);
        }

        try
        {
            var token = JsonSerializer.Deserialize<AdminToken>(_protector.Unprotect(ciphertext))
                ?? throw new InvalidDataException("The stored admin token is empty.");

            if (string.IsNullOrWhiteSpace(token.Login)
                || string.IsNullOrWhiteSpace(token.AccessToken)
                || string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidDataException("The stored admin token is missing required fields.");
            }

            _token = token;
            _loaded = true;
            return (token, false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException
            or FormatException)
        {
            // A lost key ring or damaged row is not recoverable. Drop the payload
            // so the app falls back to polling and asks the admin to sign in again.
            logger.LogWarning(
                ex,
                "The stored admin Twitch token could not be read; it was cleared. Sign in again.");
            _token = null;
            _loaded = true;
            await TryDeleteRowAsync("unreadable");
            return (null, true);
        }
    }

    private async Task<(string? AccessToken, bool Cleared)> RefreshAndPersistLockedAsync(
        AdminToken token, CancellationToken ct)
    {
        AdminToken? refreshed = await RefreshTokenAsync(token, ct);
        if (refreshed is null)
        {
            _token = null;
            _loaded = true;
            await TryDeleteRowAsync("rejected");
            return (null, true);
        }

        await PersistLockedAsync(refreshed, ct);
        _token = refreshed;
        logger.LogInformation(
            "Refreshed the admin Twitch token for {Login}.", refreshed.Login);
        return (refreshed.AccessToken, false);
    }

    private async Task<AdminToken?> RefreshTokenAsync(AdminToken token, CancellationToken ct)
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

    /// <summary>Upserts the single row with fresh ciphertext; the plaintext never leaves memory.</summary>
    private async Task PersistLockedAsync(AdminToken token, CancellationToken ct)
    {
        string ciphertext = _protector.Protect(JsonSerializer.Serialize(token));

        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        AdminTokenRow? row = await db.AdminTokens
            .SingleOrDefaultAsync(existing => existing.Id == AdminTokenRow.SingletonId, ct);
        if (row is null)
        {
            row = new AdminTokenRow { Ciphertext = ciphertext };
            db.AdminTokens.Add(row);
        }

        row.Ciphertext = ciphertext;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteRowAsync(CancellationToken ct)
    {
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        await db.AdminTokens
            .Where(row => row.Id == AdminTokenRow.SingletonId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes a token that is already unusable. The in-memory token is cleared either way, so a
    /// failed delete is logged rather than thrown; the row is only ciphertext, and the next start
    /// finds it unusable again and retries.
    /// </summary>
    private async Task TryDeleteRowAsync(string reason)
    {
        try
        {
            // Not cancellable: the caller already dropped the token and must not leave it stored.
            await DeleteRowAsync(CancellationToken.None);
        }
        catch (Exception ex) when (RecordStoreFailures.IsStoreFailure(ex))
        {
            logger.LogError(ex, "The {Reason} admin Twitch token could not be removed from the record store.", reason);
        }
    }

    private sealed record TokenRefreshResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
