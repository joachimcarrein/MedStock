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
    public static VacationNeed? Vacation(StockInput i, DateOnly today, DateOnly departure, int days, decimal portion = 1)
    {
        if (i.Paused || i.Mode == UsageMode.AsNeeded || i.UnitsPerPackage <= 0 || days < 1) return null;
        if (departure < today) departure = today;
        var eve = departure.AddDays(-1);
        var last = departure.AddDays(days - 1);
        var need = StockCalculator.UsageBetween(i, eve, last);
        var atDeparture = StockCalculator.Remaining(i, eve < today ? today : eve);
        var buy = Math.Max(0m, StockCalculator.UsageBetween(i, today, last) - StockCalculator.Remaining(i, today));
        if (portion <= 0) portion = 1;
        return new VacationNeed(need, portion, Portions(need, atDeparture, portion), atDeparture, (int)Math.Ceiling(buy / i.UnitsPerPackage));
    }

    /// <summary>
    /// Whole portions (strips, bottles) to take along for <paramref name="need"/> units. The opened portion (what is left
    /// at departure beyond whole portions) goes first: 56 doses left in a bottle and 112 needed → that bottle plus a new one.
    /// </summary>
    public static int Portions(decimal need, decimal atDeparture, decimal portion)
    {
        if (need <= 0) return 0;
        var open = atDeparture > 0 ? atDeparture % portion : 0m;
        if (open <= 0) return (int)Math.Ceiling(need / portion);
        return need <= open ? 1 : 1 + (int)Math.Ceiling((need - open) / portion);
    }

    /// <summary>
    /// The smallest amount you take along: the set value, else a whole package for bottles and inhalers (you can't take
    /// half a bottle), else one unit.
    /// </summary>
    public static decimal TakePortion(Medication m) =>
        m.TakeAlongUnits is > 0 ? Math.Min(m.TakeAlongUnits.Value, m.UnitsPerPackage)
        : m.Type is MedicationType.NasalSpray or MedicationType.Inhaler or MedicationType.Drops or MedicationType.Liquid ? m.UnitsPerPackage
        : 1m;
}

/// <summary>Portion: units per portion taken along; TakePortions: how many of them.</summary>
public sealed record VacationNeed(decimal NeedUnits, decimal Portion, int TakePortions, decimal UnitsAtDeparture, int BuyPackages);
