using MedStock.Web.Domain;

namespace MedStock.Web.Services;

public enum StockState { Empty, LastDay, Critical, AsNeededLow, Warning, Ok, AsNeededOk, Paused }

/// <summary>The inputs of the stock model for one medication (medication + its current snapshot).</summary>
public sealed record StockInput(
    UsageMode Mode,
    decimal PerDay,
    decimal[] Weekly,
    bool Paused,
    decimal MinStockUnits,
    decimal UnitsPerPackage,
    DateOnly SnapshotDate,
    decimal SnapshotUnits)
{
    public static StockInput From(Medication m, Snapshot s) =>
        new(m.UsageMode, m.PerDay, m.Weekly, m.Paused, m.MinStockUnits, m.UnitsPerPackage, s.Date, s.TotalUnits);
}

public sealed record StockResult(
    decimal Remaining,
    int? DaysRemaining,
    DateOnly? LastDoseDate,
    StockState State,
    bool CapReached,
    int Packages,
    int RestUnits)
{
    public int StateRank => StockCalculator.Rank(State);
    public int SortDays => DaysRemaining ?? int.MaxValue;
    public bool IsRed => State is StockState.Empty or StockState.LastDay or StockState.Critical or StockState.AsNeededLow;
}

/// <summary>
/// Pure stock model. A snapshot says "at the END of day D the total stock was T units".
/// remaining(today) = max(0, T − Σ usage(d) for d in (D, today]).
/// </summary>
public static class StockCalculator
{
    public const int CapDays = 3650;

    public static int WeekdayIndex(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    public static decimal Usage(StockInput i, DateOnly d) =>
        i.Paused ? 0m : i.Mode switch
        {
            UsageMode.PerDay => i.PerDay,
            UsageMode.Weekly => i.Weekly.Length == 7 ? i.Weekly[WeekdayIndex(d)] : 0m,
            _ => 0m,
        };

    /// <summary>Σ usage(d) for d in (from, to]. Zero for an empty or negative interval.</summary>
    public static decimal UsageBetween(StockInput i, DateOnly from, DateOnly to)
    {
        int days = to.DayNumber - from.DayNumber;
        if (days <= 0 || i.Paused) return 0m;
        switch (i.Mode)
        {
            case UsageMode.PerDay:
                return i.PerDay * days;
            case UsageMode.Weekly:
                if (i.Weekly.Length != 7) return 0m;
                decimal weekSum = i.Weekly.Sum();
                int fullWeeks = days / 7;
                decimal total = weekSum * fullWeeks;
                for (var d = from.AddDays(fullWeeks * 7 + 1); d <= to; d = d.AddDays(1))
                    total += i.Weekly[WeekdayIndex(d)];
                return total;
            default:
                return 0m;
        }
    }

    public static decimal Remaining(StockInput i, DateOnly at) =>
        Math.Max(0m, i.SnapshotUnits - UsageBetween(i, i.SnapshotDate, at));

    public static bool HasRegularUsage(StockInput i) => !i.Paused && i.Mode switch
    {
        UsageMode.PerDay => i.PerDay > 0,
        UsageMode.Weekly => i.Weekly.Length == 7 && i.Weekly.Any(w => w > 0),
        _ => false,
    };

    public static StockResult Calculate(StockInput i, DateOnly today, int alertDays)
    {
        decimal remaining = Remaining(i, today);
        int packages = i.UnitsPerPackage > 0 ? (int)Math.Floor(remaining / i.UnitsPerPackage) : 0;
        int rest = (int)Math.Floor(remaining - packages * i.UnitsPerPackage);

        int? days = null;
        DateOnly? lastDose = null;
        bool cap = false;
        bool lastDoseToday = false;

        if (HasRegularUsage(i))
        {
            if (i.Mode == UsageMode.PerDay)
            {
                decimal raw = Math.Floor(remaining / i.PerDay);
                if (raw >= CapDays) { cap = true; days = CapDays; }
                else days = (int)raw;
                lastDose = today.AddDays(days.Value);
            }
            else
            {
                (days, lastDose, cap) = SimulateWeekly(i, today, remaining);
                if (remaining == 0 && today > i.SnapshotDate)
                {
                    decimal usageToday = Usage(i, today);
                    if (usageToday > 0 && Remaining(i, today.AddDays(-1)) >= usageToday)
                    {
                        lastDoseToday = true;
                        lastDose = today;
                        days = 0;
                    }
                }
            }
        }

        StockState state;
        if (i.Paused) state = StockState.Paused;
        else if (remaining == 0 && !lastDoseToday) state = StockState.Empty;
        else if (lastDoseToday || (days == 0 && remaining > 0)) state = StockState.LastDay;
        else if (i.Mode == UsageMode.AsNeeded)
            state = remaining <= i.MinStockUnits ? StockState.AsNeededLow : StockState.AsNeededOk;
        else if (days is null || cap) state = StockState.Ok;
        else if (days <= alertDays) state = StockState.Critical;
        else if (days <= 2 * alertDays) state = StockState.Warning;
        else state = StockState.Ok;

        if (state == StockState.Paused) { days = null; lastDose = null; cap = false; }

        return new StockResult(remaining, days, lastDose, state, cap, packages, rest);
    }

    private static (int? days, DateOnly? lastDose, bool cap) SimulateWeekly(StockInput i, DateOnly today, decimal remaining)
    {
        var start = (today > i.SnapshotDate ? today : i.SnapshotDate).AddDays(1);
        var limit = today.AddDays(CapDays);
        decimal r = remaining;
        DateOnly? last = null;
        for (var d = start; d <= limit; d = d.AddDays(1))
        {
            decimal u = Usage(i, d);
            if (u == 0) continue;
            if (r < u) return (last is null ? 0 : last.Value.DayNumber - today.DayNumber, last, false);
            r -= u;
            last = d;
        }
        return (CapDays, last, true);
    }

    public static int Rank(StockState s) => s switch
    {
        StockState.Empty => 0,
        StockState.LastDay => 1,
        StockState.Critical => 2,
        StockState.AsNeededLow => 2,
        StockState.Warning => 3,
        StockState.Ok => 4,
        StockState.AsNeededOk => 5,
        _ => 6,
    };
}
