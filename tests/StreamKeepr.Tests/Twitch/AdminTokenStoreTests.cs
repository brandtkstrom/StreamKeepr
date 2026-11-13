using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class AdminTokenStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Save_writes_an_encrypted_owner_only_file_and_get_round_trips()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        var token = AdminTokenStoreTestData.Token(T0.AddHours(2));

        await harness.Store.SaveAsync(token, CancellationToken.None);

        Assert.True(File.Exists(harness.TokenPath));
        var ciphertext = await File.ReadAllBytesAsync(harness.TokenPath);
        var text = Encoding.UTF8.GetString(ciphertext);
        Assert.DoesNotContain(token.AccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(token.RefreshToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Login, text, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(harness.TokenPath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(harness.TokenPath)!));
        }

        var appData = Path.GetDirectoryName(harness.TokenPath)!;
        Assert.Empty(Directory.GetFiles(appData, "*.tmp"));

        Assert.Equal(token.AccessToken, await harness.Store.GetAsync(CancellationToken.None));

        using var second = harness.CreateSecondStore();
        Assert.Equal(token.AccessToken, await second.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Get_returns_null_when_no_token_exists()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());

        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));
        Assert.False(File.Exists(harness.TokenPath));
    }

    [Fact]
    public async Task Save_replaces_the_previous_token_and_raises_changed()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        var changes = 0;
        harness.Store.Changed += () => changes++;

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1), "access-1", "refresh-1"),
            CancellationToken.None);
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1), "access-2", "refresh-2"),
            CancellationToken.None);

        Assert.Equal(2, changes);
        Assert.Equal("access-2", await harness.Store.GetAsync(CancellationToken.None));

        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(harness.TokenPath));
        Assert.DoesNotContain("access-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("access-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_deletes_the_file_raises_changed_and_returns_null()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1)),
            CancellationToken.None);

        var changes = 0;
        harness.Store.Changed += () => changes++;

        await harness.Store.ClearAsync(CancellationToken.None);

        Assert.False(File.Exists(harness.TokenPath));
        Assert.Equal(1, changes);
        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));

        await harness.Store.ClearAsync(CancellationToken.None);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task Get_refreshes_only_inside_the_five_minute_window(int minutesUntilExpiry, bool shouldRefresh)
    {
        using var harness = new Harness(
            _ => AdminTokenStoreTestData.TokenResponse("rotated-access", "rotated-refresh"));
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddMinutes(minutesUntilExpiry)),
            CancellationToken.None);

        var accessToken = await harness.Store.GetAsync(CancellationToken.None);

        Assert.Equal(shouldRefresh ? "rotated-access" : "access-1", accessToken);
        Assert.Equal(shouldRefresh ? 1 : 0, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Refresh_rotates_the_pair_and_persists_it()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(AdminTokenStoreTestData.TokenResponse("access-2", "refresh-2"));
        responses.Enqueue(AdminTokenStoreTestData.TokenResponse("access-3", "refresh-3"));
        using var harness = new Harness(_ => responses.Dequeue());

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddMinutes(4), "access-1", "refresh-1"),
            CancellationToken.None);

        Assert.Equal("access-2", await harness.Store.GetAsync(CancellationToken.None));

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://id.twitch.tv/oauth2/token", request.RequestUri?.ToString());
        Assert.Contains("grant_type=refresh_token", request.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=test-client-id", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=test-client-secret", request.Body, StringComparison.Ordinal);

        // The rotated refresh token is persisted, not just the new access token.
        harness.Clock.Advance(TimeSpan.FromMinutes(57));
        Assert.Equal("access-3", await harness.Store.GetAsync(CancellationToken.None));
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Contains("refresh_token=refresh-2", harness.Handler.Requests[1].Body, StringComparison.Ordinal);

        using var second = harness.CreateSecondStore();
        Assert.Equal("access-3", await second.GetAsync(CancellationToken.None));
        Assert.Equal(2, harness.Handler.Requests.Count);

        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(harness.TokenPath));
        Assert.DoesNotContain("access-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("access-3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-3", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Refresh_rejection_clears_the_token_and_raises_changed(HttpStatusCode status)
    {
        using var harness = new Harness(_ => new HttpResponseMessage(status));
        var changes = 0;
        harness.Store.Changed += () => changes++;

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddMinutes(4)),
            CancellationToken.None);

        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));

        Assert.Equal(2, changes);
        Assert.False(File.Exists(harness.TokenPath));
        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));
        Assert.Single(harness.Handler.Requests);
        Assert.DoesNotContain(
            harness.Logs.Messages,
            message => message.Contains("access-1", StringComparison.Ordinal)
                || message.Contains("refresh-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_network_failure_clears_the_token()
    {
        using var harness = new Harness(_ => throw new HttpRequestException("refresh exploded"));

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddMinutes(4)),
            CancellationToken.None);

        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));
        Assert.False(File.Exists(harness.TokenPath));
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains("admin token refresh failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAsync_forces_a_refresh_outside_the_window_and_does_not_raise_changed()
    {
        using var harness = new Harness(
            _ => AdminTokenStoreTestData.TokenResponse("access-2", "refresh-2"));
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(2), "access-1", "refresh-1"),
            CancellationToken.None);

        var changes = 0;
        harness.Store.Changed += () => changes++;

        Assert.Equal("access-2", await harness.Store.RefreshAsync(CancellationToken.None));

        Assert.Equal(0, changes);
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Contains("refresh_token=refresh-1", request.Body, StringComparison.Ordinal);

        Assert.Equal("access-2", await harness.Store.GetAsync(CancellationToken.None));
        Assert.Single(harness.Handler.Requests);

        using var second = harness.CreateSecondStore();
        Assert.Equal("access-2", await second.GetAsync(CancellationToken.None));
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_without_http_when_no_token_exists()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        var changes = 0;
        harness.Store.Changed += () => changes++;

        Assert.Null(await harness.Store.RefreshAsync(CancellationToken.None));

        Assert.Empty(harness.Handler.Requests);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task RefreshAsync_failure_clears_the_token_and_raises_changed_once()
    {
        using var harness = new Harness(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(2)),
            CancellationToken.None);

        var changes = 0;
        harness.Store.Changed += () => changes++;

        Assert.Null(await harness.Store.RefreshAsync(CancellationToken.None));

        Assert.Equal(1, changes);
        Assert.False(File.Exists(harness.TokenPath));
        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_propagates_cancellation()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(2)),
            CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Store.RefreshAsync(cts.Token).AsTask());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Corrupt_token_file_is_logged_cleared_and_returns_null()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        Directory.CreateDirectory(Path.GetDirectoryName(harness.TokenPath)!);
        await File.WriteAllTextAsync(harness.TokenPath, "not a protected payload");

        Assert.Null(await harness.Store.GetAsync(CancellationToken.None));

        Assert.False(File.Exists(harness.TokenPath));
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains("could not be read", StringComparison.Ordinal)
                && message.Contains("Sign in again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lost_key_ring_is_logged_cleared_and_returns_null()
    {
        using var root = new TempRecordingsRoot();
        using var logs = new ListLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var handler = new RecordingHttpMessageHandler(_ => throw UnexpectedRequest());
        var clock = new ManualTimeProvider(T0);
        var keyDirectory = AppDataPaths.KeysDirectory(root.Path);
        var tokenPath = AppDataPaths.AdminTokensFile(root.Path);

        using (var originalProtection = new TestDataProtection(keyDirectory))
        {
            using var original = AdminTokenStoreTestData.CreateStore(
                root.Path,
                originalProtection.Provider,
                handler,
                clock,
                loggerFactory.CreateLogger<AdminTokenStore>());
            await original.SaveAsync(
                AdminTokenStoreTestData.Token(T0.AddHours(1)),
                CancellationToken.None);
        }

        Directory.Delete(keyDirectory, recursive: true);

        using var replacementProtection = new TestDataProtection(keyDirectory);
        using var replacement = AdminTokenStoreTestData.CreateStore(
            root.Path,
            replacementProtection.Provider,
            handler,
            clock,
            loggerFactory.CreateLogger<AdminTokenStore>());

        Assert.Null(await replacement.GetAsync(CancellationToken.None));
        Assert.False(File.Exists(tokenPath));
        Assert.Contains(
            logs.Messages,
            message => message.Contains("could not be read", StringComparison.Ordinal)
                && message.Contains("Sign in again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitForTokenAsync_completes_when_a_token_is_saved()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());

        var waiter = harness.Store.WaitForTokenAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1)),
            CancellationToken.None);

        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForTokenAsync_remains_waiting_after_a_clear()
    {
        using var harness = new Harness(_ => throw UnexpectedRequest());
        var waiter = harness.Store.WaitForTokenAsync(CancellationToken.None);

        await harness.Store.ClearAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1)),
            CancellationToken.None);

        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tokens_never_appear_in_logs()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(AdminTokenStoreTestData.TokenResponse("access-2", "refresh-2"));
        using var harness = new Harness(_ => responses.Dequeue());

        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddMinutes(4), "access-1", "refresh-1"),
            CancellationToken.None);
        Assert.Equal("access-2", await harness.Store.GetAsync(CancellationToken.None));
        await harness.Store.SaveAsync(
            AdminTokenStoreTestData.Token(T0.AddHours(1), "access-3", "refresh-3"),
            CancellationToken.None);
        await harness.Store.ClearAsync(CancellationToken.None);

        var logs = string.Join('\n', harness.Logs.Messages);
        Assert.Contains("streamer", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("access-1", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("access-2", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-2", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("access-3", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-3", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminToken_ToString_redacts_the_tokens()
    {
        var text = AdminTokenStoreTestData
            .Token(T0.AddHours(1), "access-value", "refresh-value")
            .ToString();

        Assert.Contains("streamer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-value", text, StringComparison.Ordinal);
    }

    private static InvalidOperationException UnexpectedRequest() =>
        new("The token store made an unexpected Twitch request.");

    private sealed class Harness : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;

        public Harness(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            Root = new TempRecordingsRoot();
            Logs = new ListLoggerProvider();
            _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs));
            DataProtection = new TestDataProtection(AppDataPaths.KeysDirectory(Root.Path));
            Clock = new ManualTimeProvider(T0);
            Handler = new RecordingHttpMessageHandler(respond);
            Store = CreateStore();
        }

        public TempRecordingsRoot Root { get; }

        public ListLoggerProvider Logs { get; }

        public TestDataProtection DataProtection { get; }

        public ManualTimeProvider Clock { get; }

        public RecordingHttpMessageHandler Handler { get; }

        public AdminTokenStore Store { get; }

        public string TokenPath => AppDataPaths.AdminTokensFile(Root.Path);

        public AdminTokenStore CreateSecondStore() => CreateStore();

        public void Dispose()
        {
            Store.Dispose();
            Handler.Dispose();
            DataProtection.Dispose();
            _loggerFactory.Dispose();
            Logs.Dispose();
            Root.Dispose();
        }

        private AdminTokenStore CreateStore() =>
            AdminTokenStoreTestData.CreateStore(
                Root.Path,
                DataProtection.Provider,
                Handler,
                Clock,
                _loggerFactory.CreateLogger<AdminTokenStore>());
    }
}
