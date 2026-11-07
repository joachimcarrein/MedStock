using System.Globalization;
using System.Net;
using MedStock.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Tests;

public class WebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public WebTests(AddonWebFactory f) => _f = f;

    [Fact]
    public async Task Links_carry_the_ingress_prefix()
    {
        var html = await _f.Client(ingressPath: "/api/hassio_ingress/abc123").GetStringAsync("/");
        Assert.Contains("href=\"/api/hassio_ingress/abc123/Doctor\"", html);
        Assert.Contains("href=\"/api/hassio_ingress/abc123/Settings\"", html);
        Assert.Contains("src=\"/api/hassio_ingress/abc123/lib/htmx.min.js", html);
        Assert.DoesNotContain("href=\"/Doctor\"", html);
    }

    [Fact]
    public async Task Script_urls_and_form_actions_carry_the_ingress_prefix()
    {
        var id = _f.WithDb(db =>
        {
            var m = new MedStock.Web.Domain.Medication
            {
                Profile = new MedStock.Web.Domain.Profile { Name = "Prefix" }, Name = "X", UnitsPerPackage = 10, PerDay = 1,
                Snapshots = [new MedStock.Web.Domain.Snapshot { Date = new DateOnly(2026, 11, 17), TotalUnits = 10 }],
            };
            db.Medications.Add(m);
            db.SaveChanges();
            return m.Id;
        });
        var html = await _f.Client(ingressPath: "/api/hassio_ingress/abc123").GetStringAsync($"/Medications/Details?id={id}");
        Assert.Contains($"data-upload-url=\"/api/hassio_ingress/abc123/Medications/Photo?id={id}\"", html);
        Assert.Contains($"action=\"/api/hassio_ingress/abc123/Medications/Details?id={id}&amp;handler=Purchase\"", html);
        Assert.DoesNotContain("action=\"/Medications", html);
    }

    [Theory]
    [InlineData("10.0.0.5", HttpStatusCode.Forbidden)]
    [InlineData("192.168.1.20", HttpStatusCode.Forbidden)]
    [InlineData("172.30.32.2", HttpStatusCode.OK)]
    public async Task Ingress_gate_only_admits_the_supervisor(string ip, HttpStatusCode expected)
    {
        var resp = await _f.Client(remoteIp: ip).GetAsync("/");
        Assert.Equal(expected, resp.StatusCode);
    }

    [Fact]
    public async Task Healthz_from_localhost_passes_the_gate()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.Client(remoteIp: "127.0.0.1").GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Client(remoteIp: "127.0.0.1").GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task User_header_is_upserted()
    {
        var c = _f.Client(userId: "upsert-1");
        c.DefaultRequestHeaders.Add("X-Remote-User-Display-Name", "Wife");
        await c.GetStringAsync("/");
        Assert.Equal("Wife", _f.WithDb(db => db.Users.Single(u => u.Id == "upsert-1").DisplayName));
        Assert.True(_f.WithDb(db => db.UserSettings.Any(s => s.UserId == "upsert-1")));

        var c2 = _f.Client(userId: "upsert-1");
        c2.DefaultRequestHeaders.Add("X-Remote-User-Name", "wife.b");
        await c2.GetStringAsync("/");
        Assert.Equal("wife.b", _f.WithDb(db => db.Users.Single(u => u.Id == "upsert-1").DisplayName));
    }

    [Fact]
    public async Task Missing_user_header_falls_back_to_household_with_banner()
    {
        var html = await _f.Client(userId: null).GetStringAsync("/");
        Assert.Contains("household-banner", html);
        Assert.False(_f.WithDb(db => db.UserSettings.Single(s => s.UserId == "household").NotificationsEnabled));
    }

    [Fact]
    public async Task Language_from_accept_language_and_user_setting()
    {
        var c = _f.Client(userId: "lang-1");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE,nl;q=0.9");
        Assert.Contains("Overzicht", await c.GetStringAsync("/"));

        var c2 = _f.Client(userId: "lang-2");
        c2.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        await c2.GetStringAsync("/"); // creates the user
        _f.WithDb(db =>
        {
            db.UserSettings.Single(s => s.UserId == "lang-2").Language = "en";
            return db.SaveChanges();
        });
        // The language is cached per user for an hour; a fresh factory is not needed because lang-2 was cached as null
        // only if read before; clear the cache entry like the settings page does.
        var cache = _f.Services.GetService(typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache)) as Microsoft.Extensions.Caching.Memory.IMemoryCache;
        cache!.Remove(LocalizationSetup.LanguageCacheKey("lang-2"));
        var html = await c2.GetStringAsync("/");
        Assert.Contains("Overview", html);
        Assert.DoesNotContain("Overzicht", html);
    }

    [Fact]
    public void Global_settings_are_seeded_after_migration()
    {
        var gs = _f.WithDb(db => db.GlobalSettings.Single());
        Assert.Equal(90, gs.CoverageDays);
        Assert.Equal(14, gs.DefaultAlertDays);
        Assert.Equal(new TimeOnly(9, 0), gs.NotificationTime);
        Assert.False(string.IsNullOrEmpty(gs.WrittenByVersion));
        Assert.Empty(_f.WithDb(db => db.Database.GetPendingMigrations().ToList()));
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("0,5")]
    [InlineData(" 0,5 ")]
    public async Task Decimal_binder_accepts_dot_and_comma(string raw)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(decimal));
        var ctx = new DefaultModelBindingContext
        {
            ModelMetadata = metadata,
            ModelName = "PerDay",
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(BindingSource.Query,
                new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["PerDay"] = raw }),
                new CultureInfo("nl-BE")),
        };
        await new InvariantDecimalBinder().BindModelAsync(ctx);
        Assert.True(ctx.Result.IsModelSet);
        Assert.Equal(0.5m, ctx.Result.Model);
    }

    [Fact]
    public async Task Decimal_binder_rejects_garbage()
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(decimal));
        var ctx = new DefaultModelBindingContext
        {
            ModelMetadata = metadata,
            ModelName = "PerDay",
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(BindingSource.Query,
                new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["PerDay"] = "1.000,5" }),
                CultureInfo.InvariantCulture),
        };
        await new InvariantDecimalBinder().BindModelAsync(ctx);
        Assert.False(ctx.Result.IsModelSet);
        Assert.False(ctx.ModelState.IsValid);
    }
}

public class DevModeWebTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _f;
    public DevModeWebTests(WebFactory f) => _f = f;

    [Fact]
    public async Task Dev_mode_uses_dev_user_and_simulated_prefix()
    {
        var c = _f.Client(userId: null, remoteIp: "10.0.0.5", ingressPath: null);
        var html = await c.GetStringAsync("/api/hassio_ingress/dev/");
        Assert.Contains("href=\"/api/hassio_ingress/dev/Doctor\"", html);
        Assert.True(_f.WithDb(db => db.Users.Any(u => u.Id == "dev")));
    }
}
