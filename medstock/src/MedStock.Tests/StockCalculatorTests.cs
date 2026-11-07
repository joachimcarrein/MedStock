using MedStock.Web.Services;
using static MedStock.Tests.T;

namespace MedStock.Tests;

public class StockCalculatorTests
{
    [Theory]
    [InlineData("2026-11-09", 0)]
    [InlineData("2026-11-10", 1)]
    [InlineData("2026-11-11", 2)]
    [InlineData("2026-11-12", 3)]
    [InlineData("2026-11-13", 4)]
    [InlineData("2026-11-14", 5)]
    [InlineData("2026-11-15", 6)]
    public void Weekday_index_is_monday_first(string date, int index) =>
        Assert.Equal(index, StockCalculator.WeekdayIndex(D(date)));

    [Fact]
    public void Nasalix_after_ten_days()
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 240), D("2026-11-17"), 14);
        Assert.Equal(160m, r.Remaining);
        Assert.Equal(20, r.DaysRemaining);
        Assert.Equal(D("2026-12-07"), r.LastDoseDate);
        Assert.Equal(1, r.Packages);
        Assert.Equal(20, r.RestUnits);
        Assert.Equal(StockState.Warning, r.State);
    }

    [Fact]
    public void Nasalix_checkbox_off_gives_29_days()
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 232), D("2026-11-07"), 14);
        Assert.Equal(29, r.DaysRemaining);
    }

    [Theory]
    [InlineData("2026-11-09", 3, "2026-11-28", 19, StockState.Warning)]
    [InlineData("2026-11-15", 2, "2026-11-28", 13, StockState.Critical)]
    public void Weekject_weekly(string today, int remaining, string lastDose, int days, StockState state)
    {
        var r = StockCalculator.Calculate(Weekly(Saturday, "2026-11-09", 3), D(today), 14);
        Assert.Equal(remaining, r.Remaining);
        Assert.Equal(D(lastDose), r.LastDoseDate);
        Assert.Equal(days, r.DaysRemaining);
        Assert.Equal(state, r.State);
    }

    [Fact]
    public void Weekject_last_dose_today_is_LastDay_not_Empty()
    {
        var r = StockCalculator.Calculate(Weekly(Saturday, "2026-11-09", 3), D("2026-11-28"), 14);
        Assert.Equal(0m, r.Remaining);
        Assert.Equal(StockState.LastDay, r.State);
        Assert.Equal(D("2026-11-28"), r.LastDoseDate);
        Assert.Equal(0, r.DaysRemaining);
    }

    [Fact]
    public void Weekject_day_after_last_dose_is_Empty()
    {
        var r = StockCalculator.Calculate(Weekly(Saturday, "2026-11-09", 3), D("2026-11-29"), 14);
        Assert.Equal(StockState.Empty, r.State);
    }

    [Fact]
    public void Weekly_sunday_pins_monday_index()
    {
        var r = StockCalculator.Calculate(Weekly(Sunday, "2026-11-14", 2), D("2026-11-14"), 14);
        Assert.Equal(D("2026-11-22"), r.LastDoseDate);
        Assert.Equal(8, r.DaysRemaining);
    }

    [Fact]
    public void Half_pill_per_day() =>
        Assert.Equal(40, StockCalculator.Calculate(PerDay(0.5m, "2026-11-07", 20, 30), D("2026-11-07"), 14).DaysRemaining);

    [Fact]
    public void Rounds_down() =>
        Assert.Equal(11, StockCalculator.Calculate(PerDay(2, "2026-11-07", 23, 30), D("2026-11-07"), 14).DaysRemaining);

    [Fact]
    public void Runs_empty_and_clamps_at_zero()
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 10), D("2026-11-10"), 14);
        Assert.Equal(0m, r.Remaining);
        Assert.Equal(StockState.Empty, r.State);
    }

    [Fact]
    public void Less_than_one_day_left_is_LastDay()
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 5), D("2026-11-07"), 14);
        Assert.Equal(StockState.LastDay, r.State);
        Assert.Equal(0, r.DaysRemaining);
    }

    [Fact]
    public void Today_before_snapshot_uses_total() =>
        Assert.Equal(100m, StockCalculator.Calculate(PerDay(8, "2026-11-10", 100), D("2026-11-07"), 14).Remaining);

    [Fact]
    public void Today_equals_snapshot_no_deduction() =>
        Assert.Equal(100m, StockCalculator.Calculate(PerDay(8, "2026-11-07", 100), D("2026-11-07"), 14).Remaining);

    [Fact]
    public void Weekly_all_zero_has_no_days()
    {
        var r = StockCalculator.Calculate(Weekly(new decimal[7], "2026-11-07", 5), D("2026-11-20"), 14);
        Assert.Null(r.DaysRemaining);
        Assert.Null(r.LastDoseDate);
        Assert.Equal(StockState.Ok, r.State);
    }

    [Fact]
    public void Cap_at_ten_years()
    {
        var r = StockCalculator.Calculate(PerDay(0.001m, "2026-11-07", 100), D("2026-11-07"), 14);
        Assert.True(r.CapReached);
        Assert.Equal(3650, r.DaysRemaining);
        Assert.Equal(StockState.Ok, r.State);
    }

    [Fact]
    public void Weekly_cap_at_ten_years()
    {
        var r = StockCalculator.Calculate(Weekly(Saturday, "2026-11-07", 10000), D("2026-11-07"), 14);
        Assert.True(r.CapReached);
        Assert.Equal(3650, r.DaysRemaining);
    }

    [Fact]
    public void Paused_freezes_stock()
    {
        var r = StockCalculator.Calculate(PerDay(8, "2026-11-07", 100, paused: true), D("2026-11-20"), 14);
        Assert.Equal(100m, r.Remaining);
        Assert.Equal(StockState.Paused, r.State);
        Assert.Null(r.DaysRemaining);
    }

    [Fact]
    public void Resume_continues_counting_the_day_after()
    {
        // Pause on X (snapshot X), resume on X+5 (snapshot X+5, same units): counting resumes on X+6.
        var resumed = PerDay(8, "2026-11-12", 100);
        Assert.Equal(100m, StockCalculator.Remaining(resumed, D("2026-11-12")));
        Assert.Equal(92m, StockCalculator.Remaining(resumed, D("2026-11-13")));
    }

    [Theory]
    [InlineData(6, 5, StockState.AsNeededOk)]
    [InlineData(5, 5, StockState.AsNeededLow)]
    [InlineData(0, 5, StockState.Empty)]
    public void As_needed_states(int units, int min, StockState state)
    {
        var r = StockCalculator.Calculate(AsNeeded(min, "2026-11-01", units), D("2026-11-20"), 14);
        Assert.Equal(state, r.State);
        Assert.Equal(units, r.Remaining);
        Assert.Null(r.DaysRemaining);
    }

    [Theory]
    [InlineData(StockState.Empty, 0)]
    [InlineData(StockState.LastDay, 1)]
    [InlineData(StockState.Critical, 2)]
    [InlineData(StockState.AsNeededLow, 2)]
    [InlineData(StockState.Warning, 3)]
    [InlineData(StockState.Ok, 4)]
    [InlineData(StockState.AsNeededOk, 5)]
    [InlineData(StockState.Paused, 6)]
    public void Ranks(StockState s, int rank) => Assert.Equal(rank, StockCalculator.Rank(s));

    [Fact]
    public void Usage_between_handles_long_weekly_intervals()
    {
        var i = Weekly([1, 0, 2, 0, 0, 1, 0], "2026-11-07", 1000);
        decimal loop = 0;
        for (var d = D("2026-11-08"); d <= D("2027-03-05"); d = d.AddDays(1)) loop += StockCalculator.Usage(i, d);
        Assert.Equal(loop, StockCalculator.UsageBetween(i, D("2026-11-07"), D("2027-03-05")));
    }
}
