using MedStock.Web.Domain;

namespace MedStock.Web.Services;

public static class PrescriptionCalculator
{
    public static int CoverageDays(int? medication, int? profile, int global) => medication ?? profile ?? global;

    /// <summary>Number of packages to ask the doctor for to cover <paramref name="coverageDays"/> days after today.</summary>
    public static int PackagesNeeded(StockInput i, DateOnly today, int coverageDays)
    {
        if (i.UnitsPerPackage <= 0 || i.Paused) return 0;
        decimal needed;
        if (i.Mode == UsageMode.AsNeeded)
            needed = i.MinStockUnits - i.SnapshotUnits;
        else
            needed = StockCalculator.UsageBetween(i, today, today.AddDays(coverageDays)) - StockCalculator.Remaining(i, today);
        return (int)Math.Ceiling(Math.Max(0m, needed) / i.UnitsPerPackage);
    }
}
