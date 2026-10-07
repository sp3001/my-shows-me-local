using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shows.Infrastructure;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Http;

namespace Shows.Tests.Unit;

/// <summary>
/// Step 0.4 verification: DataPaths resolves both paths under one base directory, and a named
/// client's proxy failure surfaces as a transport error naming the host (SPEC R14).
/// </summary>
public class DataPathsTests
{
    private static ServiceProvider BuildProvider(string baseDir)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shows:Data:BaseDirectory"] = baseDir,
                ["Shows:Data:DatabasePath"] = "data/shows.db",
                ["Shows:Data:ImdbDatasetPath"] = "data/imdb",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShowsInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Both_paths_resolve_under_one_base_directory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "shows-datapaths-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = BuildProvider(baseDir);
            var paths = provider.GetRequiredService<DataPaths>();

            Assert.Equal(Path.GetFullPath(baseDir), paths.BaseDirectory);
            Assert.StartsWith(Path.GetFullPath(baseDir), paths.DatabasePath, StringComparison.Ordinal);
            Assert.StartsWith(Path.GetFullPath(baseDir), paths.ImdbDatasetPath, StringComparison.Ordinal);
            Assert.StartsWith(Path.GetFullPath(baseDir), paths.LogDirectory, StringComparison.Ordinal);

            // The three must be siblings under one base, which is what makes "back up this one
            // directory" the correct instruction (SPEC P4, ARCHITECTURE.md §7).
            Assert.Equal("shows.db", Path.GetFileName(paths.DatabasePath));
            Assert.Equal("imdb", Path.GetFileName(paths.ImdbDatasetPath));
            Assert.Equal("log", Path.GetFileName(paths.LogDirectory));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Fact]
    public void An_absolute_configured_path_is_honoured_not_rebased()
    {
        // A container points the database at a volume mount with an absolute path; the base-directory
        // logic must not prepend anything to it.
        var absolute = Path.IsPathRooted(Path.GetTempPath())
            ? Path.Combine(Path.GetTempPath(), "mount", "shows.db")
            : "/mount/shows.db";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shows:Data:BaseDirectory"] = Path.GetTempPath(),
                ["Shows:Data:DatabasePath"] = absolute,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShowsInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        var paths = provider.GetRequiredService<DataPaths>();

        Assert.Equal(Path.GetFullPath(absolute), paths.DatabasePath);
    }
}

/// <summary>ProxyConfig scheme handling (SPEC R14, ADR-029).</summary>
public class ProxyConfigTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_means_direct(string? value) => Assert.Null(ProxyConfig.CreateProxy(value));

    [Theory]
    [InlineData("http://main-pc:10801")]
    [InlineData("socks5://main-pc:10801")]
    [InlineData("socks4://main-pc:10801")]
    [InlineData("main-pc:10801")] // bare host:port is treated as http
    public void Accepted_schemes_build_a_proxy(string value)
    {
        var proxy = ProxyConfig.CreateProxy(value);
        Assert.NotNull(proxy);
    }

    [Theory]
    [InlineData("garbage://main-pc:10801")]
    [InlineData("ftp://main-pc:10801")]
    public void Unsupported_scheme_throws_at_construction(string value)
    {
        // A typo fails at startup rather than silently sending traffic direct (SPEC R14).
        Assert.Throws<NotSupportedException>(() => ProxyConfig.CreateProxy(value));
    }
}

/// <summary>
/// SPEC R14's central guarantee: an unreachable host surfaces as a transport error naming the host,
/// never as an empty result. This is the only defence against "TVmaze returned nothing" silently
/// meaning "we never reached TVmaze".
/// </summary>
public class ProxyTransportErrorTests
{
    [Fact]
    public async Task A_refused_proxy_surfaces_as_a_transport_error_naming_the_host()
    {
        // 127.0.0.1:1 is a refused port: it fails fast and names itself (DATA_ANALYSIS.md §9.6),
        // which is exactly the observable the rule requires. A filtered port would instead hang to
        // the connect timeout, so the refused case is the one that can be asserted quickly and
        // deterministically here.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shows:Data:BaseDirectory"] = Path.GetTempPath(),
                ["Shows:Tvmaze:BaseUrl"] = "https://api.tvmaze.com",
                ["Shows:Proxy:Tvmaze"] = "http://127.0.0.1:1",
                ["Shows:Http:ConnectTimeoutSeconds"] = "5",
                ["Shows:Http:RequestTimeoutSeconds"] = "10",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShowsInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Tvmaze");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://api.tvmaze.com/shows/305", UriKind.Absolute),
                HttpCompletionOption.ResponseHeadersRead));

        // The failure must name the host we tried to reach through the proxy, so a log reader can
        // tell "cannot reach the proxy" from "TVmaze answered 5xx" (SPEC R14).
        Assert.Contains("127.0.0.1", ex.ToString(), StringComparison.Ordinal);

        // And it must be a thrown transport failure, not an empty result set — the distinction the
        // whole rule exists to preserve.
        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }
}
