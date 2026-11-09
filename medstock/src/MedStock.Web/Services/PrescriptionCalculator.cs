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

    /// <summary>
    /// Trip of <paramref name="days"/> days from <paramref name="departure"/> (inclusive): what is used during the trip, what is
    /// left the evening before, and how much to buy before leaving (all use from today until the last day of the trip,
    /// minus the stock today). Null for as-needed or paused medication (no fixed use).
    /// </summary>
    public static VacationNeed? Vacation(StockInput i, DateOnly today, DateOnly departure, int days)
    {
        if (i.Paused || i.Mode == UsageMode.AsNeeded || i.UnitsPerPackage <= 0 || days < 1) return null;
        if (departure < today) departure = today;
        var eve = departure.AddDays(-1);
        var last = departure.AddDays(days - 1);
        var need = StockCalculator.UsageBetween(i, eve, last);
        var atDeparture = StockCalculator.Remaining(i, eve < today ? today : eve);
        var buy = Math.Max(0m, StockCalculator.UsageBetween(i, today, last) - StockCalculator.Remaining(i, today));
        return new VacationNeed(need, (int)Math.Ceiling(need / i.UnitsPerPackage), atDeparture, (int)Math.Ceiling(buy / i.UnitsPerPackage));
    }
}

public sealed record VacationNeed(decimal NeedUnits, int NeedPackages, decimal UnitsAtDeparture, int BuyPackages);
