using System.Net;
using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MedStock.Tests;

/// <summary>Runs the real app on a temporary SQLite database with a fake Home Assistant and a fixed clock.</summary>
public class WebFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "medstock-tests", Guid.NewGuid().ToString("N"));
    public bool AddonMode { get; init; }
    public FixedClock Clock { get; } = new(new DateTime(2026, 11, 17, 10, 0, 0));
    public FakeHaClient Ha { get; } = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<FakeHaClient>.Instance);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(AddonMode ? "Production" : "Development");
        builder.UseSetting("MEDSTOCK_DATA_DIR", DataDir);
        builder.UseSetting("SUPERVISOR_TOKEN", AddonMode ? "test-token" : "");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IHaClient>();
            s.AddSingleton<IHaClient>(Ha);
            s.AddSingleton(Ha);
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(Clock);
            s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
        });
    }

    public HttpClient Client(string? userId = "u1", string? remoteIp = "172.30.32.2", string? ingressPath = "/api/hassio_ingress/test")
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        if (userId is not null) c.DefaultRequestHeaders.Add("X-Remote-User-Id", userId);
        if (remoteIp is not null) c.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        if (ingressPath is not null) c.DefaultRequestHeaders.Add("X-Ingress-Path", ingressPath);
        return c;
    }

    public T WithDb<T>(Func<MedStockDb, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<MedStockDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue(RemoteIpHeader, out var ip) && IPAddress.TryParse(ip, out var addr))
                    ctx.Connection.RemoteIpAddress = addr;
                await n();
            });
            next(app);
        };
    }
}

public sealed class AddonWebFactory : WebFactory
{
    public AddonWebFactory() { AddonMode = true; }
}
