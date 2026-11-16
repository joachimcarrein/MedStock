using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;

namespace MedStock.Tests;

/// <summary>Intake moments, "every N days/weeks" and the doses still to come today (recount and initial stock).</summary>
public class ScheduleTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public ScheduleTests(AddonWebFactory f) => _f = f;

    // 2026-11-17 is a Tuesday, 2026-11-21 a Saturday.
    private static readonly DateOnly Today = T.D("2026-11-17");

    [Fact]
    public void Every_two_days_counts_only_the_dose_days()
    {
        var i = T.PerDay(1, "2026-11-17", 5) with { Every = 2, EveryFrom = T.D("2026-11-17") };
        Assert.Equal(0m, StockCalculator.Usage(i, T.D("2026-11-18")));
        Assert.Equal(1m, StockCalculator.Usage(i, T.D("2026-11-19")));
        Assert.Equal(1m, StockCalculator.Usage(i, T.D("2026-11-15"))); // the rhythm also runs before the anchor
        Assert.Equal(5m, StockCalculator.UsageBetween(i, Today, T.D("2026-11-27"))); // 19, 21, 23, 25, 27
        var r = StockCalculator.Calculate(i, Today, 14);
        Assert.Equal(T.D("2026-11-27"), r.LastDoseDate);
        Assert.Equal(10, r.DaysRemaining);
        Assert.Equal(StockState.Critical, r.State);
    }

    [Theory]
    [InlineData("2026-11-21")] // the dose day itself
    [InlineData("2026-11-18")] // any day of the same week
    public void Every_two_weeks_uses_the_week_of_the_anchor(string anchor)
    {
        var i = T.Weekly(T.Saturday, "2026-11-17", 3) with { Every = 2, EveryFrom = T.D(anchor) };
        Assert.Equal(1m, StockCalculator.Usage(i, T.D("2026-11-21")));
        Assert.Equal(0m, StockCalculator.Usage(i, T.D("2026-11-28")));
        Assert.Equal(1m, StockCalculator.Usage(i, T.D("2026-12-05")));
        var r = StockCalculator.Calculate(i, Today, 14);
        Assert.Equal(T.D("2026-12-19"), r.LastDoseDate); // 21 Nov, 5 Dec, 19 Dec
        Assert.Equal(32, r.DaysRemaining);
    }

    [Fact]
    public void Prescription_and_trip_follow_the_rhythm()
    {
        var weekly = T.Weekly(T.Saturday, "2026-11-17", 3);
        var biweekly = weekly with { Every = 2, EveryFrom = T.D("2026-11-21") };
        // 90 days: 21 Nov, 5 and 19 Dec, 2, 16 and 30 Jan, 13 Feb = 7 doses − 3 in stock = 4 → one pack of 4.
        Assert.Equal(1, PrescriptionCalculator.PackagesNeeded(biweekly, Today, 90));
        Assert.Equal(3, PrescriptionCalculator.PackagesNeeded(weekly, Today, 90)); // 13 Saturdays − 3 = 10 → 3 packs

        // Trip 1–14 Dec: every two weeks only 5 Dec; every week 5 and 12 Dec.
        Assert.Equal(1m, PrescriptionCalculator.Vacation(biweekly, Today, T.D("2026-12-01"), 14)!.NeedUnits);
        Assert.Equal(2m, PrescriptionCalculator.Vacation(weekly, Today, T.D("2026-12-01"), 14)!.NeedUnits);

        var everyOtherDay = T.PerDay(1, "2026-11-17", 10, upp: 30) with { Every = 2, EveryFrom = T.D("2026-11-17") };
        var trip = PrescriptionCalculator.Vacation(everyOtherDay, Today, T.D("2026-11-20"), 7)!; // 20–26 Nov: 21, 23, 25
        Assert.Equal(3m, trip.NeedUnits);
        Assert.Equal(0, trip.BuyPackages);
    }

    private static Medication Med(UsageMode mode = UsageMode.PerDay, decimal perDay = 0, List<DoseMoment>? moments = null,
        TimeOnly? weeklyTime = null, int every = 1, DateOnly? from = null) => new()
    {
        UsageMode = mode, PerDay = moments?.Sum(x => x.Amount) ?? perDay, Moments = moments ?? [],
        Weekly = mode == UsageMode.Weekly ? T.Saturday : new decimal[7], WeeklyTime = weeklyTime, Every = every, EveryFrom = from,
        UnitsPerPackage = 30,
    };

    private static decimal Pending(Medication m, string at) =>
        DoseSchedule.PendingToday(m, DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(20, 0)).Sum(x => x.Amount);

    [Theory]
    [InlineData("2026-11-17 07:00", 2)]
    [InlineData("2026-11-17 08:00", 1)] // the 08:00 dose counts as taken at 08:00
    [InlineData("2026-11-17 19:59", 1)]
    [InlineData("2026-11-17 20:00", 0)]
    public void Doses_still_to_come_follow_the_intake_times(string at, decimal expected) =>
        Assert.Equal(expected, Pending(Med(moments: [new(new TimeOnly(8, 0), 1), new(new TimeOnly(20, 0), 1)]), at));

    [Fact]
    public void Untimed_weekly_and_rhythm_cases()
    {
        Assert.Equal(2m, Pending(Med(perDay: 2), "2026-11-17 10:00")); // old medication: one untimed moment
        Assert.Equal(0m, Pending(Med(perDay: 2), "2026-11-17 21:00"));
        Assert.Equal(1m, Pending(Med(UsageMode.Weekly, weeklyTime: new TimeOnly(9, 0)), "2026-11-21 08:30"));
        Assert.Equal(0m, Pending(Med(UsageMode.Weekly, weeklyTime: new TimeOnly(9, 0)), "2026-11-21 09:30"));
        Assert.Equal(0m, Pending(Med(UsageMode.Weekly, weeklyTime: new TimeOnly(9, 0)), "2026-11-17 08:00")); // no dose on Tuesday
        Assert.Equal(0m, Pending(Med(perDay: 1, every: 2, from: T.D("2026-11-16")), "2026-11-17 10:00")); // not a dose day
        Assert.Equal(1m, Pending(Med(perDay: 1, every: 2, from: T.D("2026-11-15")), "2026-11-17 10:00"));
        Assert.Equal(0m, Pending(Med(UsageMode.AsNeeded), "2026-11-17 10:00"));
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static FormUrlEncodedContent Form(IEnumerable<(string, string)> fields) =>
        new(fields.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

    [Fact]
    public async Task Old_medication_shows_its_daily_amount_as_one_moment_and_times_alone_write_no_snapshot()
    {
        var id = _f.WithDb(db =>
        {
            var m = new Medication
            {
                Profile = new Profile { Name = "Sched" }, Name = "Bisoprolol", Type = MedicationType.Pill, UnitsPerPackage = 28, PerDay = 2,
                Snapshots = [new Snapshot { Date = Today, TotalUnits = 50, Reason = SnapshotReason.Initial }],
            };
            db.Medications.Add(m);
            db.SaveChanges();
            return m.Id;
        });
        var c = _f.Client(ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        var html = WebUtility.HtmlDecode(await c.GetStringAsync($"/Medications/Edit?id={id}"));
        Assert.Contains("name=\"Input.Moments[0].Amount\" value=\"2\"", html);
        Assert.Contains("name=\"Input.Moments[0].Time\" aria-label", html); // no value: no time yet
        Assert.DoesNotContain("Help mij rekenen", html);
        var profileId = _f.WithDb(db => db.Medications.Single(m => m.Id == id).ProfileId);
        var snapshotId = _f.WithDb(db => db.Snapshots.Where(s => s.MedicationId == id).Max(s => s.Id));

        List<(string, string)> Fields(params (string, string)[] extra) =>
        [
            ("__RequestVerificationToken", Token(html)), ("Input.Id", id.ToString()), ("Input.ExpectedSnapshotId", snapshotId.ToString()),
            ("Input.Profile", profileId.ToString()), ("Input.Name", "Bisoprolol"), ("Input.Type", "Pill"), ("Input.UnitsPerPackage", "28"),
            ("Input.UsageMode", "PerDay"), ("Input.NotificationsEnabled", "true"), .. extra,
        ];

        // Same total, now with times (and an empty extra row): no new snapshot.
        var resp = await c.PostAsync("/Medications/Edit", Form(Fields(("Input.Every", "1"),
            ("Input.Moments[0].Time", "08:00"), ("Input.Moments[0].Amount", "1"), ("Input.Moments[1].Time", "20:00"), ("Input.Moments[1].Amount", "1"),
            ("Input.Moments[2].Time", ""), ("Input.Moments[2].Amount", ""))));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        _f.WithDb(db =>
        {
            var m = db.Medications.Single(x => x.Id == id);
            Assert.Equal([new(new TimeOnly(8, 0), 1m), new(new TimeOnly(20, 0), 1m)], m.Moments);
            Assert.Equal(2m, m.PerDay);
            Assert.Equal(1, db.Snapshots.Count(s => s.MedicationId == id));
            return 0;
        });

        // Every 2 days without a start date is refused; with one it is a usage change (DoseChange snapshot).
        var fields = Fields(("Input.Every", "2"), ("Input.Moments[0].Time", "08:00"), ("Input.Moments[0].Amount", "1"));
        resp = await c.PostAsync("/Medications/Edit", Form(fields));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        fields.Add(("Input.EveryFrom", "2026-11-17"));
        resp = await c.PostAsync("/Medications/Edit", Form(fields));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        _f.WithDb(db =>
        {
            var m = db.Medications.Single(x => x.Id == id);
            Assert.Equal(2, m.Every);
            Assert.Equal(Today, m.EveryFrom);
            Assert.Equal(1m, m.PerDay);
            Assert.Equal(SnapshotReason.DoseChange, db.Snapshots.Where(s => s.MedicationId == id).OrderByDescending(s => s.Id).First().Reason);
            return 0;
        });
    }

    [Fact]
    public async Task Overview_lists_the_intake_moments_still_to_come_today()
    {
        using var f = new AddonWebFactory();
        f.Clock.Now = new DateTime(2026, 11, 17, 10, 0, 0);
        f.WithDb(db =>
        {
            var p = new Profile { Name = "Husband" };
            db.Medications.Add(new Medication
            {
                Profile = p, Name = "Bisoprolol", Type = MedicationType.Pill, UnitsPerPackage = 28, PerDay = 2,
                Moments = [new(new TimeOnly(8, 0), 1), new(new TimeOnly(20, 0), 1)],
                Snapshots = [new Snapshot { Date = Today, TotalUnits = 50 }],
            });
            db.Medications.Add(new Medication
            {
                Profile = p, Name = "Untimed", Type = MedicationType.Pill, UnitsPerPackage = 28, PerDay = 1,
                Snapshots = [new Snapshot { Date = Today, TotalUnits = 50 }],
            });
            return db.SaveChanges();
        });
        var c = f.Client(ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/"));
        Assert.Contains("Vandaag nog te nemen", html);
        Assert.Contains("<strong>20:00</strong>", html);
        Assert.DoesNotContain("<strong>08:00</strong>", html); // already past
        Assert.Contains("1 pil", html);

        f.Clock.Now = new DateTime(2026, 11, 17, 20, 30, 0);
        Assert.DoesNotContain("Vandaag nog te nemen", await c.GetStringAsync("/"));
    }
}
