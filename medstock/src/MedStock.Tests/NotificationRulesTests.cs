using System.Globalization;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using static MedStock.Tests.T;

namespace MedStock.Tests;

public class NotificationRulesTests
{
    [Theory]
    [InlineData(14, new[] { 14, 7, 3, 0 })]
    [InlineData(3, new[] { 3, 1, 0 })]
    [InlineData(1, new[] { 1, 0 })]
    [InlineData(0, new[] { 0 })]
    public void Levels(int alertDays, int[] expected) => Assert.Equal(expected, NotificationRules.Levels(alertDays));

    /// <summary>Runs the rules once and applies the decision to the simulated log, like NotificationService does.</summary>
    private static int? Run(StockInput input, DateOnly today, HashSet<int> log, int alertDays = 14)
    {
        var r = StockCalculator.Calculate(input, today, alertDays);
        var active = NotificationRules.ActiveLevels(input.Mode, r, input.MinStockUnits, alertDays);
        switch (NotificationRules.Decide(active, log))
        {
            case NotificationDecision.ClearLog: log.Clear(); return null;
            case NotificationDecision.Send s: foreach (var l in s.ActiveLevels) log.Add(l); return s.Level;
            default: return null;
        }
    }

    [Fact]
    public void Progression_sends_exactly_on_14_7_and_0()
    {
        var log = new HashSet<int>();
        var sent = new List<int>();
        foreach (var days in new[] { 20, 14, 10, 7, 0 })
        {
            // remaining = days * 8 at 8/day; for 0 the stock is empty
            var input = PerDay(8, "2026-11-07", days * 8);
            if (Run(input, D("2026-11-07"), log) is int lvl) sent.Add(lvl);
        }
        Assert.Equal([14, 7, 0], sent);
    }

    [Fact]
    public void Jump_straight_to_five_days_sends_once_for_level_7()
    {
        var log = new HashSet<int>();
        Assert.Equal(7, Run(PerDay(8, "2026-11-07", 40), D("2026-11-07"), log));
        Assert.Equal([7, 14], log.Order());
        Assert.Null(Run(PerDay(8, "2026-11-07", 40), D("2026-11-07"), log));
    }

    [Fact]
    public void Recount_on_same_level_does_not_repeat()
    {
        var log = new HashSet<int>();
        Assert.Equal(14, Run(PerDay(8, "2026-11-07", 100), D("2026-11-07"), log)); // 12 days
        Assert.Null(Run(PerDay(8, "2026-11-07", 90), D("2026-11-07"), log));      // recount: 11 days
    }

    [Fact]
    public void Purchase_above_threshold_rearms()
    {
        var log = new HashSet<int>();
        Assert.Equal(14, Run(PerDay(8, "2026-11-07", 100), D("2026-11-07"), log));
        Assert.Null(Run(PerDay(8, "2026-11-07", 1000), D("2026-11-07"), log)); // bought: 125 days
        Assert.Empty(log);
        Assert.Equal(14, Run(PerDay(8, "2026-11-07", 100), D("2026-11-07"), log)); // later back down
    }

    [Fact]
    public void As_needed_below_minimum_sends_once()
    {
        var log = new HashSet<int>();
        var sent = new List<int?>();
        foreach (var units in new[] { 5, 4, 3 })
            sent.Add(Run(AsNeeded(5, "2026-11-07", units), D("2026-11-07"), log));
        Assert.Equal([-1, null, null], sent);
    }

    [Fact]
    public void Cap_and_no_usage_are_never_active()
    {
        var r = StockCalculator.Calculate(PerDay(0.001m, "2026-11-07", 100), D("2026-11-07"), 14);
        Assert.Empty(NotificationRules.ActiveLevels(UsageMode.PerDay, r, 0, 14));
    }

    [Fact]
    public void Eligibility()
    {
        var m = new Medication { UsageMode = UsageMode.PerDay, PerDay = 1, NotificationsEnabled = true };
        var input = PerDay(1, "2026-11-07", 10);
        Assert.True(NotificationRules.IsEligible(m, input));
        m.NotificationsEnabled = false;
        Assert.False(NotificationRules.IsEligible(m, input));
        m.NotificationsEnabled = true; m.Archived = true;
        Assert.False(NotificationRules.IsEligible(m, input));
        m.Archived = false;
        Assert.False(NotificationRules.IsEligible(m, PerDay(0, "2026-11-07", 10)));
    }

    [Fact]
    public void Recipients()
    {
        var users = new[]
        {
            new UserSettings { UserId = "a", NotificationsEnabled = true, NotifyServices = ["notify.a1", "notify.a2"] },
            new UserSettings { UserId = "b", NotificationsEnabled = false, NotifyServices = ["notify.b"] },
            new UserSettings { UserId = "c", NotificationsEnabled = true, NotifyServices = ["notify.c"], NotifyProfileIds = [2] },
            new UserSettings { UserId = "d", NotificationsEnabled = true, NotifyServices = [] },
        };
        Assert.Equal([("a", "notify.a1"), ("a", "notify.a2")], NotificationRules.Recipients(users, 1).ToList());
        Assert.Equal([("a", "notify.a1"), ("a", "notify.a2"), ("c", "notify.c")], NotificationRules.Recipients(users, 2).ToList());
    }

    [Theory]
    [InlineData("2026-11-07 08:59", null, false)]
    [InlineData("2026-11-07 09:00", null, true)]
    [InlineData("2026-11-07 08:59", "2026-11-06 09:00", false)]
    [InlineData("2026-11-07 09:30", "2026-11-06 09:00", true)]
    [InlineData("2026-11-07 08:00", "2026-11-07 07:00", false)]
    [InlineData("2026-11-07 09:30", "2026-11-07 09:01", false)]
    [InlineData("2026-11-07 09:30", "2026-11-07 08:00", true)] // time moved later on the same day
    public void Scheduler_matrix(string now, string? last, bool expected)
    {
        DateTime P(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expected, NotificationScheduler.ShouldRun(P(now), new TimeOnly(9, 0), last is null ? null : P(last)));
    }

    public static TheoryData<string, string> Messages => new()
    {
        { "nl", "Nasalix (Husband): nog 12 dagen" },
        { "en", "Nasalix (Husband): 12 days left" },
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Message_per_day(string lang, string expected)
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 100), D("2026-11-07"), 14);
        Assert.Equal(expected, Texts.NotificationMessage("Nasalix", "Husband", MedicationType.NasalSpray, UsageMode.PerDay, r, new CultureInfo(lang)));
    }

    [Theory]
    [InlineData("nl", "Weekject (Husband): nog 2 spuiten, laatste dosis {0}")]
    [InlineData("en", "Weekject (Husband): 2 injections left, last dose {0}")]
    public void Message_weekly(string lang, string expected)
    {
        var c = new CultureInfo(lang);
        var r = StockCalculator.Calculate(Weekly(Saturday, "2026-11-09", 3), D("2026-11-15"), 14);
        Assert.Equal(string.Format(expected, D("2026-11-28").ToString("ddd d MMM", c)),
            Texts.NotificationMessage("Weekject", "Husband", MedicationType.Injection, UsageMode.Weekly, r, c));
    }

    [Theory]
    [InlineData("nl", "Paracetamol (Wife): nog 6 pillen (zo nodig)")]
    [InlineData("en", "Paracetamol (Wife): 6 pills left (as needed)")]
    public void Message_as_needed(string lang, string expected)
    {
        var r = StockCalculator.Calculate(AsNeeded(10, "2026-11-07", 6), D("2026-11-07"), 14);
        Assert.Equal(expected, Texts.NotificationMessage("Paracetamol", "Wife", MedicationType.Pill, UsageMode.AsNeeded, r, new CultureInfo(lang)));
    }

    [Theory]
    [InlineData("nl", "Nasalix (Husband): is op")]
    [InlineData("en", "Nasalix (Husband): out of stock")]
    public void Message_empty(string lang, string expected)
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 0), D("2026-11-08"), 14);
        Assert.Equal(expected, Texts.NotificationMessage("Nasalix", "Husband", MedicationType.NasalSpray, UsageMode.PerDay, r, new CultureInfo(lang)));
    }

    [Theory]
    [InlineData("nl", 1, 20, "1 flesje + 20 doses")]
    [InlineData("nl", 2, 0, "2 flesjes")]
    [InlineData("nl", 0, 20, "20 doses")]
    [InlineData("en", 1, 1, "1 bottle + 1 dose")]
    public void Quantity(string lang, int packages, int rest, string expected)
    {
        var r = new StockResult(packages * 140 + rest, null, null, StockState.Ok, false, packages, rest);
        Assert.Equal(expected, Texts.Quantity(MedicationType.NasalSpray, r, new CultureInfo(lang)));
    }
}
