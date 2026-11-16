using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

internal static class MudTestServices
{
    /// <summary>MudBlazor's services with the app's own configuration, as <c>Program.cs</c> registers them.</summary>
    public static IServiceCollection AddStreamKeeprMudServices(this IServiceCollection services) =>
        services.AddMudServices(options => StreamKeeprSnackbars.Configure(options.SnackbarConfiguration));
}
