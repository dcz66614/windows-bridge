using Bridge.RelayService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// The real relay on a real Kestrel socket (WebSocket tests need a
/// real transport; TestServer cannot upgrade). Fresh temp DB per
/// fixture instance; Development so plain-HTTP loopback is allowed.
/// </summary>
public sealed class KestrelFixture : IAsyncLifetime
{
    public string BaseUrl { get; private set; } = "";
    public string DbPath { get; } = Path.Combine(
        Path.GetTempPath(), "bridge-relay-kestrel-" + Guid.NewGuid().ToString("N") + ".db");

    private WebApplication? _app;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        _app = RelayApp.CreateApp(new[] { "--environment", "Development" }, builder =>
        {
            // WebApplicationFactory points the content root at the
            // service PROJECT dir; do the same so Razor Pages
            // (login, _LoginPartial.cshtml) resolve. The test bin dir
            // has no Pages/ folder.
            builder.Environment.ContentRootPath = FindServiceProjectDir();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RelayService:DatabasePath"] = DbPath,
                ["RelayService:Urls"] = "http://127.0.0.1:0",
            });
        });
        await RelayApp.InitializeAsync(_app);
        await _app.StartAsync();
        BaseUrl = _app.Urls.First(u => u.StartsWith("http://127.0.0.1", StringComparison.Ordinal));
    }

    private static string FindServiceProjectDir()
    {
        var dir = Path.GetDirectoryName(typeof(KestrelFixture).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Bridge.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new InvalidOperationException("Could not locate Bridge.sln.");
        return Path.Combine(dir, "src", "Bridge.RelayService");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { File.Delete(DbPath); } catch { }
        try { File.Delete(DbPath + "-wal"); } catch { }
        try { File.Delete(DbPath + "-shm"); } catch { }
    }
}
