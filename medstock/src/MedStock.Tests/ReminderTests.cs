using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MedStock.Tests;

/// <summary>Intake reminders: when, to whom, grouping, discreet text, Taken and Later.</summary>
public class ReminderTests
{
    private const string Phone = "notify.mobile_app_dev_iphone";
    private const string Android = "notify.mobile_app_dev_android";

    private sealed class Env : IDisposable
    {
        public readonly AddonWebFactory F = new();
        public readonly int ProfileId, Bisoprolol, Vitamin, Untimed;

        public Env()
        {
            F.Clock.Now = new DateTime(2026, 11, 17, 7, 0, 0); // Tuesday
            (ProfileId, Bisoprolol, Vitamin, Untimed) = F.WithDb(db =>
            {
                var p = new Profile { Name = "Husband" };
                Medication Med(string name, decimal perDay, params DoseMoment[] moments) => new()
                {
                    Profile = p, Name = name, Type = MedicationType.Pill, UnitsPerPackage = 28, UsageMode = UsageMode.PerDay,
                    PerDay = perDay, Moments = [.. moments],
                    Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = 100, Reason = SnapshotReason.Initial }],
                };
                var a = Med("Bisoprolol", 2, new(new TimeOnly(8, 0), 1), new(new TimeOnly(20, 0), 1));
                var b = Med("Vitamin D", 1, new DoseMoment(new TimeOnly(8, 0), 1));
                var c = Med("Untimed", 1);
                db.Medications.AddRange(a, b, c);
                db.SaveChanges();
                return (p.Id, a.Id, b.Id, c.Id);
            });
        }

        public void User(string id, int[] profiles, string[]? services = null, bool discreet = false) => F.WithDb(db =>
        {
            db.Users.Add(new User { Id = id, DisplayName = id });
            db.UserSettings.Add(new UserSettings
            {
                UserId = id, NotifyServices = [.. services ?? [Phone]], ReminderProfileIds = [.. profiles], Language = "en",
                DevicesInitialized = true, DiscreetNotifications = discreet,
            });
            return db.SaveChanges();
        });

        public void At(int hour, int minute = 0) => F.Clock.Now = new DateTime(2026, 11, 17, hour, minute, 0);

        public async Task<int> RunAsync()
        {
            using var scope = F.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync();
        }

        public async Task<bool> ActionAsync(string action, string? user)
        {
            using var scope = F.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationActionHandler>().HandleAsync(action, user);
        }

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task One_grouped_reminder_per_moment_and_only_once()
    {
        using var e = new Env();
        e.User("u1", [e.ProfileId], [Phone, Android]);
        e.At(7, 59);
        Assert.Equal(0, await e.RunAsync());
        e.At(8);
        Assert.Equal(1, await e.RunAsync());
        Assert.Equal(2, e.F.Ha.Sent.Count); // both devices
        var (service, title, message, link, tag) = e.F.Ha.Sent[0];
        Assert.Equal("Time for medication", title);
        Assert.Equal("08:00 Husband: Bisoprolol (1 pill), Vitamin D (1 pill)", message); // not the untimed one
        Assert.Equal($"medstock-r-{e.ProfileId}-20261117-0800", tag);
        Assert.Equal("/app/local_medstock", link);
        var actions = e.F.Ha.SentActions[0]!;
        Assert.Equal(["Taken", "30 min", "1 h", "2 h"], actions.Select(a => a.Title));
        Assert.Equal($"MEDSTOCK_TAKEN_{e.ProfileId}_20261117_0800", actions[0].Action);
        Assert.Equal($"MEDSTOCK_SNOOZE_30_{e.ProfileId}_20261117_0800", actions[1].Action);

        e.At(8, 1);
        Assert.Equal(0, await e.RunAsync());
        e.At(20);
        Assert.Equal(1, await e.RunAsync());
        Assert.Equal("20:00 Husband: Bisoprolol (1 pill)", e.F.Ha.Sent[^1].Message);
    }

    [Fact]
    public async Task A_missed_moment_is_sent_up_to_30_minutes_late()
    {
        using var e = new Env();
        e.User("u1", [e.ProfileId]);
        e.At(8, 29);
        Assert.Equal(1, await e.RunAsync());
        using var late = new Env();
        late.User("u1", [late.ProfileId]);
        late.At(8, 30);
        Assert.Equal(0, await late.RunAsync());
    }

    [Fact]
    public async Task Exceptions_per_medication_override_the_profile_choice()
    {
        using var e = new Env();
        e.User("off-but-one", []);
        e.User("on-but-one", [e.ProfileId], [Android]);
        e.User("nothing", []);
        e.F.WithDb(db =>
        {
            db.MedicationReminders.Add(new MedicationReminder { UserId = "off-but-one", MedicationId = e.Vitamin, Enabled = true });
            db.MedicationReminders.Add(new MedicationReminder { UserId = "on-but-one", MedicationId = e.Vitamin, Enabled = false });
            return db.SaveChanges();
        });
        e.At(8);
        Assert.Equal(2, await e.RunAsync());
        Assert.Contains((Phone, "08:00 Husband: Vitamin D (1 pill)"), e.F.Ha.Sent.Select(s => (s.Service, s.Message)));
        Assert.Contains((Android, "08:00 Husband: Bisoprolol (1 pill)"), e.F.Ha.Sent.Select(s => (s.Service, s.Message)));
    }

    [Fact]
    public async Task Discreet_reminders_have_no_medication_names()
    {
        using var e = new Env();
        e.User("u1", [e.ProfileId], discreet: true);
        e.At(8);
        await e.RunAsync();
        Assert.Equal("08:00 Husband", Assert.Single(e.F.Ha.Sent).Message);
    }

    [Fact]
    public async Task Taken_clears_the_reminder_everywhere_and_later_sends_it_again()
    {
        using var e = new Env();
        e.User("u1", [e.ProfileId], [Phone]);
        e.User("u2", [e.ProfileId], [Android]);
        e.At(8);
        await e.RunAsync();
        var tag = $"medstock-r-{e.ProfileId}-20261117-0800";

        Assert.True(await e.ActionAsync($"MEDSTOCK_SNOOZE_30_{e.ProfileId}_20261117_0800", "u1"));
        e.At(8, 29);
        await e.RunAsync();
        Assert.Equal(2, e.F.Ha.Sent.Count);
        e.At(8, 30);
        await e.RunAsync();
        Assert.Equal(3, e.F.Ha.Sent.Count);
        Assert.Equal((Phone, tag), (e.F.Ha.Sent[^1].Service, e.F.Ha.Sent[^1].Tag)); // only to the user who asked
        Assert.Empty(e.F.WithDb(db => db.SnoozedReminders.ToList()));

        // Later again, then Taken by the other user: the pending one is cancelled and every device is cleared.
        Assert.True(await e.ActionAsync($"MEDSTOCK_SNOOZE_60_{e.ProfileId}_20261117_0800", "u1"));
        Assert.True(await e.ActionAsync($"MEDSTOCK_TAKEN_{e.ProfileId}_20261117_0800", "u2"));
        Assert.Equal([(Android, tag), (Phone, tag)], e.F.Ha.Cleared.OrderBy(c => c.Service));
        Assert.Empty(e.F.WithDb(db => db.SnoozedReminders.ToList()));
        e.At(9, 30);
        await e.RunAsync();
        Assert.Equal(3, e.F.Ha.Sent.Count);
        Assert.Equal(1, e.F.WithDb(db => db.Snapshots.Count(s => s.MedicationId == e.Bisoprolol))); // stock untouched
    }

    [Theory]
    [InlineData("MEDSTOCK_SNOOZE_45_1_20261117_0800")] // not one of the offered delays
    [InlineData("MEDSTOCK_TAKEN_1_2026111_0800")]
    [InlineData("MEDSTOCK_TAKEN_x_20261117_0800")]
    public async Task Malformed_reminder_actions_are_ignored(string action)
    {
        using var e = new Env();
        Assert.False(await e.ActionAsync(action, "u1"));
    }

    [Fact]
    public async Task Rhythm_pause_and_weekly_time_are_respected()
    {
        using var e = new Env();
        e.User("u1", [e.ProfileId]);
        e.F.WithDb(db =>
        {
            db.Medications.Single(m => m.Id == e.Vitamin).Paused = true;
            var b = db.Medications.Single(m => m.Id == e.Bisoprolol);
            b.Every = 2;
            b.EveryFrom = T.D("2026-11-16"); // dose days 16, 18, ...: not today
            db.Medications.Add(new Medication
            {
                ProfileId = e.ProfileId, Name = "Weekject", Type = MedicationType.Injection, UnitsPerPackage = 4, UsageMode = UsageMode.Weekly,
                Weekly = [0, 1, 0, 0, 0, 0, 0], WeeklyTime = new TimeOnly(8, 0), // Tuesday
                Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = 3, Reason = SnapshotReason.Initial }],
            });
            return db.SaveChanges();
        });
        e.At(8);
        await e.RunAsync();
        Assert.Equal("08:00 Husband: Weekject (1 injection)", Assert.Single(e.F.Ha.Sent).Message);
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Settings_and_detail_screen_store_the_choices_per_user()
    {
        using var e = new Env();
        var c = e.F.Client("me", ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var settings = await c.GetStringAsync("/Settings");
        Assert.Contains("Intake reminders for", settings);
        var resp = await c.PostAsync("/Settings?handler=User", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(settings), ["notificationsEnabled"] = "true", ["services"] = Phone,
            ["reminderProfiles"] = e.ProfileId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal([e.ProfileId], e.F.WithDb(db => db.UserSettings.Single(s => s.UserId == "me").ReminderProfileIds));

        var detail = WebUtility.HtmlDecode(await c.GetStringAsync($"/Medications/Details?id={e.Vitamin}"));
        Assert.Contains("As in my settings (on)", detail);
        resp = await c.PostAsync($"/Medications/Details?id={e.Vitamin}&handler=Reminder", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(detail), ["reminder"] = "off",
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.False(e.F.WithDb(db => db.MedicationReminders.Single(r => r.UserId == "me").Enabled));
        Assert.Contains("Reminders need an intake time", WebUtility.HtmlDecode(await c.GetStringAsync($"/Medications/Details?id={e.Untimed}")));

        resp = await c.PostAsync($"/Medications/Details?id={e.Vitamin}&handler=Reminder", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(detail), ["reminder"] = "auto",
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Empty(e.F.WithDb(db => db.MedicationReminders.ToList()));
    }
}
