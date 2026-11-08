using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Settings;

/// <summary>Settings (§3.7): per HA user (devices, profiles, language, test) and global (one row).</summary>
public class IndexModel(
    MedStockDb db, CurrentUser user, AppMode mode, IHaClient ha, IMemoryCache cache, NotificationService notifications, SensorSignal sensors, SensorStatus sensorStatus,
    IStringLocalizer<SharedResource> l, ILogger<IndexModel> log) : PageModel
{
    public UserSettings Mine { get; private set; } = new();
    public GlobalSettings Global { get; private set; } = new();
    public List<Profile> Profiles { get; private set; } = [];
    /// <summary>All notify services, mobile_app first; null when Home Assistant could not be reached.</summary>
    public List<string>? Services { get; private set; }
    public List<User> KnownUsers { get; private set; } = [];
    /// <summary>todo.* entities; null when Home Assistant could not be reached.</summary>
    public IReadOnlyList<(string EntityId, string Name)>? TodoLists { get; private set; }
    public bool CanChooseUser => mode.AddonMode && !user.FromHeader;
    public bool IsHousehold => user.IsHousehold;
    public string UserName => user.DisplayName;

    public string? UserMessage { get; private set; }
    public string? UserError { get; private set; }
    public string? GlobalMessage { get; private set; }
    public string? GlobalError { get; private set; }
    public RunReport? Report { get; private set; }
    public SensorStatus Sensors => sensorStatus;

    public static bool IsMobile(string service) => service.StartsWith("notify.mobile_app_", StringComparison.Ordinal);

    /// <summary>A device that is selected but no longer exists in HA (or failed with 400/404 in a run).</summary>
    public bool IsStale(string service) =>
        Mine.StaleNotifyServices.Contains(service) || (Services is not null && !Services.Contains(service));

    /// <summary>First visit: the phones (mobile_app) are preselected until the user saves once.</summary>
    public bool IsSelected(string service) =>
        Mine.DevicesInitialized ? Mine.NotifyServices.Contains(service) : IsMobile(service);

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostUserAsync(bool notificationsEnabled, string[] services, int[] profiles, string? language)
    {
        var s = await db.UserSettings.FirstAsync(x => x.UserId == user.Id);
        s.NotificationsEnabled = notificationsEnabled;
        s.NotifyServices = services.Where(x => x.StartsWith("notify.", StringComparison.Ordinal)).Distinct().ToList();
        s.StaleNotifyServices.RemoveAll(x => !s.NotifyServices.Contains(x)); // deselected: nothing to warn about anymore
        var known = await db.Profiles.Select(p => p.Id).ToListAsync();
        var chosen = profiles.Where(known.Contains).Distinct().ToList();
        s.NotifyProfileIds = chosen.Count == known.Count ? [] : chosen; // all ticked = "all", so new profiles are included
        s.Language = language is "nl" or "en" ? language : null;
        s.DevicesInitialized = true;
        await db.SaveChangesAsync();
        cache.Remove(LocalizationSetup.LanguageCacheKey(user.Id));
        log.LogInformation("User {UserId} saved notification settings ({Devices} devices)", user.Id, s.NotifyServices.Count);
        return RedirectToPage(new { saved = "user" });
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        await LoadAsync();
        if (Mine.NotifyServices.Count == 0)
        {
            UserError = l["Settings_NoDevices"];
            return Page();
        }
        var slug = await ha.GetSelfSlugAsync(HttpContext.RequestAborted);
        var culture = NotificationService.CultureFor(Mine);
        var results = new List<string>();
        foreach (var service in Mine.NotifyServices)
        {
            var outcome = await ha.NotifyAsync(service, Texts.Get("Notify_Title", culture), Texts.Get("Notify_Test", culture),
                slug is null ? null : "/app/" + slug, "medstock-test", ct: HttpContext.RequestAborted);
            results.Add(outcome switch
            {
                NotifyOutcome.Ok => $"{service}: ✓",
                NotifyOutcome.UnknownService => $"{service}: {l["Settings_DeviceGone"]}",
                _ => $"{service}: {l["Ha_Unreachable"]}",
            });
        }
        UserMessage = string.Join(" · ", results);
        return Page();
    }

    public async Task<IActionResult> OnPostGlobalAsync(int? coverageDays, int? defaultAlertDays, string? notificationTime, bool sensorsEnabled, string? todoEntityId)
    {
        if (coverageDays is not (>= 0 and <= 3650) || defaultAlertDays is not (>= 0 and <= 3650)
            || !TimeOnly.TryParse(notificationTime, System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            await LoadAsync();
            GlobalError = $"{l["CoverageDays"]} / {l["AlertDays"]}: {l["Val_Range"]}";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        var g = await db.GlobalSettings.SingleAsync();
        g.CoverageDays = coverageDays.Value;
        g.DefaultAlertDays = defaultAlertDays.Value;
        g.NotificationTime = new TimeOnly(time.Hour, time.Minute);
        g.SensorsEnabled = sensorsEnabled;
        g.TodoEntityId = todoEntityId is { Length: > 5 and <= 255 } && todoEntityId.StartsWith("todo.", StringComparison.Ordinal) ? todoEntityId : null;
        await db.SaveChangesAsync();
        sensors.Request();
        log.LogInformation("Global settings saved");
        return RedirectToPage(new { saved = "global" });
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        Report = await notifications.RunAsync(HttpContext.RequestAborted);
        await LoadAsync();
        return Page();
    }

    /// <summary>Household fallback (no HA user header): pick an existing user or type a name; remembered in a cookie.</summary>
    public async Task<IActionResult> OnPostChooseAsync(string? userId, string? newName)
    {
        if (!CanChooseUser) return BadRequest();
        string? id = null;
        if (!string.IsNullOrWhiteSpace(newName))
        {
            id = "local-" + Guid.NewGuid().ToString("N")[..8];
            var now = DateTime.Now;
            db.Users.Add(new User { Id = id, DisplayName = newName.Trim()[..Math.Min(100, newName.Trim().Length)], FirstSeen = now, LastSeen = now });
            db.UserSettings.Add(new UserSettings { UserId = id });
            await db.SaveChangesAsync();
        }
        else if (userId is { Length: > 0 } && userId != CurrentUser.HouseholdId && await db.Users.AnyAsync(u => u.Id == userId))
            id = userId;

        var options = new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true,
            Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        };
        if (id is null) Response.Cookies.Delete(CurrentUser.ChooseCookie, options);
        else Response.Cookies.Append(CurrentUser.ChooseCookie, id, options);
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Mine = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id) ?? new UserSettings { UserId = user.Id };
        Global = await db.GlobalSettings.AsNoTracking().SingleAsync();
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        if (CanChooseUser)
            KnownUsers = await db.Users.AsNoTracking().Where(u => u.Id != CurrentUser.HouseholdId).OrderBy(u => u.DisplayName).ToListAsync();
        try
        {
            var all = await ha.GetNotifyServicesAsync(HttpContext.RequestAborted);
            Services = all.OrderBy(s => IsMobile(s) ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal).ToList();
        }
        catch (HaUnavailableException)
        {
            Services = null;
        }
        try { TodoLists = await ha.GetTodoListsAsync(HttpContext.RequestAborted); }
        catch (HaUnavailableException) { TodoLists = null; }
        var saved = Request.Query["saved"].ToString();
        if (saved == "user") UserMessage = l["Saved"];
        if (saved == "global") GlobalMessage = l["Saved"];
    }
}
