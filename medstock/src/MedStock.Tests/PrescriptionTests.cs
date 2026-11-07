using MedStock.Web.Services;
using static MedStock.Tests.T;

namespace MedStock.Tests;

public class PrescriptionTests
{
    [Fact]
    public void Nasalix_90_days_needs_4_bottles() =>
        Assert.Equal(4, PrescriptionCalculator.PackagesNeeded(PerDay(8, "2026-11-17", 160), D("2026-11-17"), 90));

    [Fact]
    public void Weekject_90_days_needs_3_packs() =>
        Assert.Equal(3, PrescriptionCalculator.PackagesNeeded(Weekly(Saturday, "2026-11-09", 3), D("2026-11-17"), 90));

    [Fact]
    public void Enough_stock_needs_nothing() =>
        Assert.Equal(0, PrescriptionCalculator.PackagesNeeded(PerDay(1, "2026-11-17", 500), D("2026-11-17"), 90));

    [Fact]
    public void Exact_multiple_is_not_rounded_up() =>
        // 280 needed (2/day for 140 days, nothing left) at 140 per package -> exactly 2
        Assert.Equal(2, PrescriptionCalculator.PackagesNeeded(PerDay(2, "2026-11-17", 0), D("2026-11-17"), 140));

    [Fact]
    public void Checkpoint_B_nasalix_50_left_needs_5() =>
        Assert.Equal(5, PrescriptionCalculator.PackagesNeeded(PerDay(8, "2026-11-17", 50), D("2026-11-17"), 90));

    [Fact]
    public void Paused_needs_nothing() =>
        Assert.Equal(0, PrescriptionCalculator.PackagesNeeded(PerDay(8, "2026-11-17", 0, paused: true), D("2026-11-17"), 90));

    [Theory]
    [InlineData(3, 10, 20, 1)]
    [InlineData(10, 10, 20, 0)]
    [InlineData(0, 45, 20, 3)]
    public void As_needed_tops_up_to_minimum(int units, int min, int upp, int expected) =>
        Assert.Equal(expected, PrescriptionCalculator.PackagesNeeded(AsNeeded(min, "2026-11-01", units, upp), D("2026-11-17"), 90));

    [Theory]
    [InlineData(30, 60, 90, 30)]
    [InlineData(null, 60, 90, 60)]
    [InlineData(null, null, 90, 90)]
    public void Coverage_cascade(int? med, int? profile, int global, int expected) =>
        Assert.Equal(expected, PrescriptionCalculator.CoverageDays(med, profile, global));
}
