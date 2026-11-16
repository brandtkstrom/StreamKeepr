using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Tests.Hosting;

public sealed class LinksOptionsTests
{
    [Fact]
    public void The_shipped_configuration_links_the_repository_and_twitch()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestPaths.RepositoryRoot(), "src", "StreamKeepr", "appsettings.json"))
            .Build();

        LinksOptions links = Resolve(configuration);

        Assert.Equal("https://github.com/brandtkstrom/StreamKeepr", links.GitHub);
        Assert.Equal("https://www.twitch.tv/", links.Twitch);
    }

    [Theory]
    [InlineData("GitHub", "")]
    [InlineData("GitHub", "/relative")]
    [InlineData("Twitch", "javascript:alert(1)")]
    [InlineData("Twitch", "ftp://twitch.tv")]
    public void A_link_must_be_an_absolute_http_url(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["Links:GitHub"] = "https://github.com/example/repo",
            ["Links:Twitch"] = "https://www.twitch.tv/",
            [$"Links:{key}"] = value,
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<OptionsValidationException>(() => Resolve(configuration));
        Assert.Contains($"Links:{key}", error.Message);
    }

    private static LinksOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLinksOptions();
        return services.BuildServiceProvider().GetRequiredService<IOptions<LinksOptions>>().Value;
    }
}
