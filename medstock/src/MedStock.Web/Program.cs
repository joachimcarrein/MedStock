using MedStock.Web.Data;
using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration -------------------------------------------------------------------------
builder.Configuration.AddJsonFile("/data/options.json", optional: true, reloadOnChange: false);
var addonMode = !string.IsNullOrEmpty(builder.Configuration["SUPERVISOR_TOKEN"]);
var isDev = builder.Environment.IsDevelopment();
var demo = args.Contains("--demo");

ApplyLogLevel(builder.Configuration, builder.Configuration["log_level"]);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = null;
    o.IncludeScopes = false;
});

var dataDir = builder.Configuration["MEDSTOCK_DATA_DIR"] is { Length: > 0 } configured
    ? configured
    : addonMode
        ? "/data"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MedStock", "dev");
var paths = new AppPaths(dataDir);
Directory.CreateDirectory(paths.DataDir);
Directory.CreateDirectory(paths.PhotosDir);
Directory.CreateDirectory(paths.KeysDir);

// ---- Services ------------------------------------------------------------------------------
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new AppMode(addonMode));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<MedicationService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<NotificationState>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddScoped<NotificationActionHandler>();
if (addonMode) builder.Services.AddHostedService<HaEventListener>();
builder.Services.AddSingleton<SensorSignal>();
builder.Services.AddSingleton<SensorStatus>();
builder.Services.AddScoped<SensorSync>();
builder.Services.AddSingleton<Func<IMqttConnection>>(() => new MqttNetConnection());
builder.Services.AddHostedService<SensorWorker>();
// The only external services (Wikimedia Commons, DailyMed), and only when the user taps "search" for a picture.
// Wikimedia asks for a descriptive User-Agent.
builder.Services.AddHttpClient<IImageSearch, ImageSearchService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"MedStock/{DbInitializer.AppVersion} (+https://github.com/joachimcarrein/MedStock)");
});
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<MedStockDb>(o => o.UseSqlite($"Data Source={paths.DbPath}"));

builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(8);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir))
    .SetApplicationName("MedStock");
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(o => o.DataAnnotationLocalizerProvider = (_, f) => f.Create(typeof(MedStock.Web.SharedResource)))
    .AddMvcOptions(o => o.ModelBinderProviders.Insert(0, new InvariantDecimalBinderProvider()));

if (addonMode)
{
    var token = builder.Configuration["SUPERVISOR_TOKEN"];
    builder.Services.AddHttpClient<IHaClient, HaClient>(c =>
        {
            c.DefaultRequestHeaders.Authorization = new("Bearer", token);
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .RedactLoggedHeaders(["Authorization"])
        .AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            o.Retry.MaxRetryAttempts = 3;
        });
    // The typed client is transient; keep one cached slug for the process.
}
else
{
    builder.Services.AddSingleton<FakeHaClient>();
    builder.Services.AddSingleton<IHaClient>(sp => sp.GetRequiredService<FakeHaClient>());
}

var app = builder.Build();

// ---- Startup -------------------------------------------------------------------------------
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MedStock");
log.LogInformation("MedStock {Version} starting (add-on mode: {AddonMode}, environment: {Env})",
    DbInitializer.AppVersion, addonMode, app.Environment.EnvironmentName);
log.LogInformation("Local time {Now:yyyy-MM-dd HH:mm}, time zone {Tz}", DateTime.Now, TimeZoneInfo.Local.Id);
if (TimeZoneInfo.Local.Id is "UTC" or "Etc/UTC" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))
    && Environment.GetEnvironmentVariable("TZ") is not ("UTC" or "Etc/UTC"))
    log.LogWarning("TZ is set to {Tz} but the process runs in UTC: tzdata missing? Dates may be wrong.", Environment.GetEnvironmentVariable("TZ"));

await DbInitializer.InitializeAsync(app.Services, paths, log);
if (demo) await DemoData.SeedAsync(app.Services);

app.Lifetime.ApplicationStopping.Register(SqliteConnection.ClearAllPools);

// ---- Pipeline ------------------------------------------------------------------------------
if (addonMode) app.UseIngressGate();
app.UseIngressPathBase(development: isDev && !addonMode);
if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
else app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.MapGet("/healthz", () => Results.Text("ok"));
app.UseHaIdentity();
app.UseStaticFiles();
app.UseRequestLocalization(LocalizationSetup.Options());
app.UseLastCulture();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorPages();

await app.RunAsync();

static void ApplyLogLevel(ConfigurationManager config, string? level)
{
    var mapped = level?.ToLowerInvariant() switch
    {
        "trace" => "Trace",
        "debug" => "Debug",
        "info" => "Information",
        "warning" => "Warning",
        "error" => "Error",
        _ => null,
    };
    if (mapped is null) return;
    var verbose = mapped is "Trace" or "Debug";
    config["Logging:LogLevel:Default"] = mapped;
    foreach (var cat in new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Net.Http", "Microsoft.Extensions.Http" })
        config[$"Logging:LogLevel:{cat}"] = verbose ? mapped : "Warning";
}

public partial class Program;
