using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MedStock.Tests;

/// <summary>The daily run (§3.8) end to end against the fake Home Assistant, each test on a fresh database.</summary>
public class NotificationServiceTests
{
    private const string Phone = "notify.mobile_app_dev_iphone";

    private sealed class Env : IDisposable
    {
        public readonly AddonWebFactory F = new();
        public int MedId;

        public Env(UsageMode mode = UsageMode.PerDay, decimal units = 20, decimal min = 0, string[]? services = null, string? language = "nl")
        {
            F.Clock.Now = new DateTime(2026, 11, 17, 10, 0, 0);
            MedId = F.WithDb(db =>
            {
                var p = new Profile { Name = "Husband" };
                var m = new Medication
                {
                    Profile = p, Name = "Nasalix", Type = MedicationType.NasalSpray, UnitsPerPackage = 140, UsageMode = mode,
                    PerDay = mode == UsageMode.PerDay ? 1 : 0, MinStockUnits = min,
                    Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = units, Reason = SnapshotReason.Initial }],
                };
                db.Medications.Add(m);
                db.Users.Add(new User { Id = "u1", DisplayName = "Husband" });
                db.UserSettings.Add(new UserSettings { UserId = "u1", NotifyServices = [.. services ?? [Phone]], Language = language, DevicesInitialized = true });
                db.SaveChanges();
                return m.Id;
            });
        }

        public async Task<RunReport> RunAsync()
        {
            using var scope = F.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationService>().RunAsync();
        }

        public async Task<RunReport?> RunIfDueAsync()
        {
            using var scope = F.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationService>().RunIfDueAsync();
        }

        public async Task Do(Func<MedicationService, Task> action)
        {
            using var scope = F.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<MedicationService>());
        }

        public int LatestSnapshot => F.WithDb(db => db.Snapshots.Where(s => s.MedicationId == MedId).Max(s => s.Id));
        public void Day(int offset) => F.Clock.Now = new DateTime(2026, 11, 17, 10, 0, 0).AddDays(offset);
        public List<string> Messages => F.Ha.Sent.Select(s => s.Message).ToList();
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task Progress_20_14_10_7_0_sends_exactly_at_14_7_and_0()
    {
        using var e = new Env(); // 20 units at 1/day, alert 14 → levels [14, 7, 3, 0]
        var sentPerDay = new Dictionary<int, int>();
        foreach (var day in new[] { 0, 6, 10, 13, 20 }) // days left: 20, 14, 10, 7, 0
        {
            e.Day(day);
            var before = e.F.Ha.Sent.Count;
            await e.RunAsync();
            await e.RunAsync(); // a second run on the same day never repeats
            sentPerDay[20 - day] = e.F.Ha.Sent.Count - before;
        }
        Assert.Equal(new Dictionary<int, int> { [20] = 0, [14] = 1, [10] = 0, [7] = 1, [0] = 1 }, sentPerDay);
        Assert.Equal("Nasalix (Husband): nog 14 dagen", e.Messages[0]);
        Assert.Equal("Nasalix (Husband): is op", e.Messages[2]);
        Assert.All(e.F.Ha.Sent, s => Assert.Equal("/app/local_medstock", s.DeepLink));
        Assert.All(e.F.Ha.Sent, s => Assert.Equal($"medstock-{e.MedId}", s.Tag));
    }

    [Fact]
    public async Task Recount_on_the_same_level_does_not_repeat_and_purchase_rearms()
    {
        using var e = new Env();
        e.Day(6); // 14 days left
        await e.RunAsync();
        Assert.Single(e.F.Ha.Sent);

        await e.Do(s => s.RecountAsync(e.MedId, e.LatestSnapshot, 13, 0, true)); // 13 days: still level 14
        await e.RunAsync();
        Assert.Single(e.F.Ha.Sent);

        await e.Do(s => s.PurchaseAsync(e.MedId, 1)); // 153 days: above the threshold → log cleared
        await e.RunAsync();
        Assert.Single(e.F.Ha.Sent);
        Assert.Empty(e.F.WithDb(db => db.NotificationLogs.Where(l => l.MedicationId == e.MedId).ToList()));

        await e.Do(s => s.RecountAsync(e.MedId, e.LatestSnapshot, 12, 0, true)); // back under 14
        await e.RunAsync();
        Assert.Equal(2, e.F.Ha.Sent.Count);
    }

    [Fact]
    public async Task As_needed_three_intakes_under_the_minimum_send_one_message()
    {
        using var e = new Env(UsageMode.AsNeeded, units: 12, min: 10);
        for (var i = 0; i < 3; i++)
        {
            await e.Do(s => s.IntakeAsync(e.MedId, 1));
            await e.RunAsync();
        }
        Assert.Single(e.F.Ha.Sent);
        Assert.Equal("Nasalix (Husband): nog 10 doses (zo nodig)", e.Messages[0]);
    }

    [Fact]
    public async Task Unknown_device_is_skipped_and_marked_while_others_receive()
    {
        using var e = new Env(services: [Phone, "notify.mobile_app_gone"], language: "en");
        e.Day(6);
        var report = await e.RunAsync();
        Assert.Equal(1, report.Deliveries);
        Assert.False(report.TransportError);
        Assert.Equal("Nasalix (Husband): 14 days left", e.Messages.Single());
        Assert.Equal(["notify.mobile_app_gone"], e.F.WithDb(db => db.UserSettings.Single(u => u.UserId == "u1").StaleNotifyServices));
        Assert.Single(e.F.WithDb(db => db.NotificationLogs.Where(l => l.MedicationId == e.MedId && l.Level == 14).ToList()));
    }

    [Fact]
    public async Task Transport_error_keeps_LastRunAt_so_the_next_tick_retries()
    {
        using var e = new Env();
        e.Day(6);
        e.F.Ha.Unavailable = true;
        Assert.NotNull(await e.RunIfDueAsync());
        Assert.Null(e.F.WithDb(db => db.GlobalSettings.Single().LastRunAt));
        Assert.Empty(e.F.WithDb(db => db.NotificationLogs.ToList())); // nothing delivered → nothing logged

        e.F.Ha.Unavailable = false;
        e.F.Clock.Now = e.F.Clock.Now.AddMinutes(1);
        await e.RunIfDueAsync();
        Assert.Single(e.F.Ha.Sent);
        Assert.Equal(e.F.Clock.Now, e.F.WithDb(db => db.GlobalSettings.Single().LastRunAt));
        Assert.Null(await e.RunIfDueAsync()); // done for today
    }

    [Fact]
    public async Task Run_waits_for_the_notification_time_and_catches_up_after_a_restart()
    {
        using var e = new Env();
        e.Day(6);
        e.F.Clock.Now = e.F.Clock.Now.Date.AddHours(8).AddMinutes(59);
        Assert.Null(await e.RunIfDueAsync());
        e.F.Clock.Now = e.F.Clock.Now.Date.AddHours(15); // app was down at 09:00
        Assert.NotNull(await e.RunIfDueAsync());
        Assert.Single(e.F.Ha.Sent);
    }

    [Fact]
    public async Task Users_with_notifications_off_or_another_profile_receive_nothing()
    {
        using var e = new Env();
        e.F.WithDb(db =>
        {
            var u = db.UserSettings.Single(s => s.UserId == "u1");
            u.NotifyProfileIds = [9999];
            return db.SaveChanges();
        });
        e.Day(6);
        var report = await e.RunAsync();
        Assert.Empty(e.F.Ha.Sent);
        Assert.Empty(e.F.WithDb(db => db.NotificationLogs.ToList())); // re-tried once someone subscribes
        Assert.Contains(report.Lines, l => l.Contains("no recipients"));
    }

    [Fact]
    public async Task Notification_carries_a_purchase_button_and_a_tap_books_one_package()
    {
        using var e = new Env();
        e.Day(6);
        await e.RunAsync();
        var action = Assert.Single(e.F.Ha.SentActions.Single()!);
        Assert.Equal($"MEDSTOCK_PURCHASE_{e.MedId}", action.Action);
        Assert.Equal("+1 flesje", action.Title);

        using var scope = e.F.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<NotificationActionHandler>();
        Assert.True(await handler.HandleAsync(action.Action, "ha-user-1"));
        var latest = e.F.WithDb(db => db.Snapshots.Where(s => s.MedicationId == e.MedId).OrderByDescending(s => s.Id).First());
        Assert.Equal(SnapshotReason.Purchase, latest.Reason);
        Assert.Equal(14m + 140m, latest.TotalUnits);
        Assert.Equal("ha-user-1", latest.CreatedByUserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OTHER_ACTION")]
    [InlineData("MEDSTOCK_PURCHASE_")]
    [InlineData("MEDSTOCK_PURCHASE_-3")]
    [InlineData("MEDSTOCK_PURCHASE_12abc")]
    [InlineData("MEDSTOCK_PURCHASE_999999")] // unknown medication
    public async Task Foreign_or_invalid_actions_are_ignored(string? action)
    {
        using var e = new Env();
        using var scope = e.F.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<NotificationActionHandler>().HandleAsync(action, null));
        Assert.Equal(1, e.F.WithDb(db => db.Snapshots.Count()));
    }

    [Fact]
    public async Task Low_medication_is_put_on_the_chosen_todo_list_without_duplicates()
    {
        using var e = new Env();
        var list = e.F.Ha.Todo["todo.shopping_list"];
        e.Day(6);
        await e.RunAsync();
        Assert.Empty(list); // no list chosen

        e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().TodoEntityId = "todo.shopping_list";
            db.NotificationLogs.RemoveRange(db.NotificationLogs);
            return db.SaveChanges();
        });
        await e.RunAsync();
        Assert.Equal(["Nasalix (Husband)"], list);

        e.Day(13); // 7-day level: still open on the list, no duplicate
        await e.RunAsync();
        Assert.Single(list);

        list.Clear(); // checked off while still low
        e.Day(17); // 3-day level
        await e.RunAsync();
        Assert.Equal(["Nasalix (Husband)"], list);
    }

    [Fact]
    public async Task Medication_that_was_already_alerted_goes_on_a_newly_chosen_list_once()
    {
        using var e = new Env();
        e.F.Ha.Todo["todo.other"] = [];
        var list = e.F.Ha.Todo["todo.shopping_list"];
        e.Day(6);
        await e.RunAsync(); // 14-day alert sent, no list chosen yet
        Assert.Single(e.F.Ha.Sent);

        e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().TodoEntityId = "todo.shopping_list";
            return db.SaveChanges();
        });
        e.Day(7);
        var report = await e.RunAsync(); // no new alert, but it is low: on the list
        Assert.Single(e.F.Ha.Sent);
        Assert.Equal(["Nasalix (Husband)"], list);
        Assert.Contains(report.Lines, l => l.Contains("added to the to-do list"));

        list.Clear(); // checked off: not back every day, only at the next alert
        e.Day(8);
        await e.RunAsync();
        Assert.Empty(list);

        // Another list chosen in Settings: the markers are reset there; simulate the same here.
        e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().TodoEntityId = "todo.other";
            db.NotificationLogs.RemoveRange(db.NotificationLogs.Where(l => l.Level == NotificationService.TodoMarkerLevel));
            return db.SaveChanges();
        });
        await e.RunAsync();
        Assert.Equal(["Nasalix (Husband)"], e.F.Ha.Todo["todo.other"]);
    }

    [Fact]
    public async Task Logs_of_a_run_contain_no_medication_or_profile_names()
    {
        using var e = new Env(services: [Phone, "notify.mobile_app_gone"]);
        e.Day(6);
        await e.RunAsync();
        e.Day(20);
        await e.RunAsync();
        var lines = e.F.Logs.Lines.Where(l => l.Level >= LogLevel.Information && !l.Category.Contains("FakeHaClient")).ToList();
        Assert.Contains(lines, l => l.Message.Contains("Notification run"));
        Assert.DoesNotContain(lines, l => l.Message.Contains("Nasalix") || l.Message.Contains("Husband"));
    }
}

public class SettingsWebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public SettingsWebTests(AddonWebFactory f) => _f = f;

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static FormUrlEncodedContent Form(string token, params (string, string)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))]);

    [Fact]
    public async Task First_visit_preselects_phones_and_save_stores_devices_and_language()
    {
        var c = _f.Client("set-1", ingressPath: null);
        var html = await c.GetStringAsync("/Settings");
        Assert.Matches("value=\"notify.mobile_app_dev_iphone\"[^>]*checked", html);
        Assert.DoesNotMatch("value=\"notify.persistent_notification\"[^>]*checked", html);

        var resp = await c.PostAsync("/Settings?handler=User", Form(Token(html),
            ("notificationsEnabled", "true"), ("services", "notify.mobile_app_dev_iphone"), ("language", "nl")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var s = _f.WithDb(db => db.UserSettings.Single(x => x.UserId == "set-1"));
        Assert.Equal(["notify.mobile_app_dev_iphone"], s.NotifyServices);
        Assert.Equal("nl", s.Language);
        Assert.True(s.DevicesInitialized);
        Assert.Contains("Instellingen", await c.GetStringAsync("/Settings")); // language applied immediately
    }

    [Fact]
    public async Task Test_notification_goes_to_my_devices_and_reports_missing_ones()
    {
        var c = _f.Client("set-2", ingressPath: null);
        var html = await c.GetStringAsync("/Settings");
        var none = await c.PostAsync("/Settings?handler=Test", Form(Token(html)));
        Assert.Contains("Select and save at least one device first", await none.Content.ReadAsStringAsync());

        await c.PostAsync("/Settings?handler=User", Form(Token(html), ("notificationsEnabled", "true"),
            ("services", "notify.mobile_app_dev_android"), ("services", "notify.mobile_app_old_phone")));
        var before = _f.Ha.Sent.Count;
        var resp = await c.PostAsync("/Settings?handler=Test", Form(Token(html)));
        var body = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Equal(before + 1, _f.Ha.Sent.Count);
        Assert.Contains("notify.mobile_app_dev_android: ✓", body);
        Assert.Contains("notify.mobile_app_old_phone: Device not found anymore", body);
        Assert.Contains("Device not found anymore; choose again", body); // marked in the device list
    }

    [Fact]
    public async Task Home_assistant_unreachable_shows_inline_message()
    {
        var c = _f.Client("set-3", ingressPath: null);
        _f.Ha.Unavailable = true;
        try
        {
            var resp = await c.GetAsync("/Settings");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("Home Assistant not reachable", await resp.Content.ReadAsStringAsync());
        }
        finally { _f.Ha.Unavailable = false; }
    }

    [Fact]
    public async Task Global_settings_are_validated_and_saved()
    {
        var c = _f.Client("set-4", ingressPath: null);
        var html = await c.GetStringAsync("/Settings");
        var bad = await c.PostAsync("/Settings?handler=Global", Form(Token(html), ("coverageDays", "5000"), ("defaultAlertDays", "14"), ("notificationTime", "09:00")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await c.PostAsync("/Settings?handler=Global", Form(Token(html), ("coverageDays", "60"), ("defaultAlertDays", "10"), ("notificationTime", "08:30")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var g = _f.WithDb(db => db.GlobalSettings.Single());
        Assert.Equal((60, 10, new TimeOnly(8, 30)), (g.CoverageDays, g.DefaultAlertDays, g.NotificationTime));
        // restore defaults for other tests in this class
        await c.PostAsync("/Settings?handler=Global", Form(Token(html), ("coverageDays", "90"), ("defaultAlertDays", "14"), ("notificationTime", "09:00")));
    }

    [Fact]
    public async Task Todo_list_and_sensors_are_saved_in_global_settings()
    {
        var c = _f.Client("set-5", ingressPath: null);
        var html = await c.GetStringAsync("/Settings");
        Assert.Contains("<option value=\"todo.shopping_list\"", html);
        await c.PostAsync("/Settings?handler=Global", Form(Token(html), ("coverageDays", "90"), ("defaultAlertDays", "14"),
            ("notificationTime", "09:00"), ("sensorsEnabled", "true"), ("todoEntityId", "todo.shopping_list")));
        var g = _f.WithDb(db => db.GlobalSettings.Single());
        Assert.True(g.SensorsEnabled);
        Assert.Equal("todo.shopping_list", g.TodoEntityId);

        await c.PostAsync("/Settings?handler=Global", Form(Token(html), ("coverageDays", "90"), ("defaultAlertDays", "14"),
            ("notificationTime", "09:00"), ("todoEntityId", "light.kitchen")));
        g = _f.WithDb(db => db.GlobalSettings.Single());
        Assert.False(g.SensorsEnabled);
        Assert.Null(g.TodoEntityId); // only todo.* entities are accepted
    }

    [Fact]
    public async Task Household_can_choose_who_they_are()
    {
        var c = _f.Client(userId: null, ingressPath: null);
        var html = await c.GetStringAsync("/Settings");
        Assert.Contains("Who are you?", html);
        var resp = await c.PostAsync("/Settings?handler=Choose", Form(Token(html), ("newName", "Grandma")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var cookie = resp.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("medstock_user="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Grandma", await c.GetStringAsync("/Settings"));
        Assert.DoesNotContain("household-banner", await c.GetStringAsync("/"));
    }
}
