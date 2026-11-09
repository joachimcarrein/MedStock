using MedStock.Web.Domain;
using MedStock.Web.Services;
using static MedStock.Tests.T;

namespace MedStock.Tests;

/// <summary>A course with an end date (e.g. antibiotics): no use after the last day.</summary>
public class CourseTests
{
    private static StockInput Course(decimal units, string end) =>
        new(UsageMode.PerDay, 3, new decimal[7], false, 0, 20, D("2026-11-17"), units, D(end));

    [Fact]
    public void Enough_until_the_end_is_covered_without_a_countdown()
    {
        var r = StockCalculator.Calculate(Course(21, "2026-11-24"), D("2026-11-17"), 14);
        Assert.Equal(StockState.CourseCovered, r.State);
        Assert.Null(r.DaysRemaining);
        Assert.Equal(D("2026-11-24"), r.LastDoseDate);
        Assert.Empty(NotificationRules.ActiveLevels(UsageMode.PerDay, r, 0, 14));
    }

    [Fact]
    public void Running_out_before_the_end_counts_down_as_usual()
    {
        var i = Course(15, "2026-11-24");
        var r = StockCalculator.Calculate(i, D("2026-11-17"), 14);
        Assert.Equal(StockState.Critical, r.State);
        Assert.Equal(5, r.DaysRemaining);
        Assert.Equal(1, PrescriptionCalculator.PackagesNeeded(i, D("2026-11-17"), 90)); // 21 − 15 = 6, not 270 − 15
    }

    [Fact]
    public void After_the_last_day_the_course_has_ended_and_stock_stays()
    {
        var i = Course(30, "2026-11-24");
        var r = StockCalculator.Calculate(i, D("2026-11-25"), 14);
        Assert.Equal(StockState.CourseEnded, r.State);
        Assert.Equal(9m, r.Remaining);
        Assert.Equal(9m, StockCalculator.Remaining(i, D("2026-12-31")));
        Assert.Equal(StockState.CourseCovered, StockCalculator.Calculate(i, D("2026-11-24"), 14).State); // last day itself
    }

    [Fact]
    public void Weekly_course_is_covered_when_the_last_dose_fits()
    {
        var i = new StockInput(UsageMode.Weekly, 0, Saturday, false, 0, 4, D("2026-11-17"), 2, D("2026-11-30"));
        var r = StockCalculator.Calculate(i, D("2026-11-17"), 14);
        Assert.Equal(StockState.CourseCovered, r.State); // Sat 21 and 28 Nov
        Assert.Equal(StockState.Critical, StockCalculator.Calculate(i with { SnapshotUnits = 1 }, D("2026-11-17"), 14).State);
    }
}
