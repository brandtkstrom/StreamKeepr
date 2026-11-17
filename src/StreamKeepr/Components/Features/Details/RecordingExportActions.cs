using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MudBlazor;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Components.Features.Details;

/// <summary>
/// Starts an MP4 export for the summary's button and the body's Retry. One copy of the
/// user-safe failure handling keeps the two call sites agreeing on the messages.
/// </summary>
internal static class RecordingExportActions
{
    public static async Task<ExportJob?> StartAsync(
        IExportService exports,
        MudBlazor.ISnackbar snackbars,
        ILogger logger,
        string recordingId,
        Task<AuthenticationState>? authenticationState)
    {
        if (authenticationState is null)
            return null;

        try
        {
            ClaimsPrincipal user = (await authenticationState).User;
            return exports.Start(recordingId, user, CancellationToken.None);
        }
        catch (Exception ex) when (IsUserSafe(ex))
        {
            // The service messages are user-safe; the disabled button is never the only guard.
            snackbars.Show(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Starting an export for {RecordingId} failed.", recordingId);
            snackbars.Show("Could not start the export. Try again.");
        }

        return null;
    }

    /// <summary>The export service's expected failures, whose messages may be shown as they are.</summary>
    public static bool IsUserSafe(Exception ex) =>
        ex is InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException;
}
