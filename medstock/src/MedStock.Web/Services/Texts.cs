using System.Globalization;
using System.Resources;
using MedStock.Web.Domain;

namespace MedStock.Web.Services;

/// <summary>Culture-explicit access to the shared resources (used outside of a request, e.g. notifications).</summary>
public static class Texts
{
    private static readonly ResourceManager Rm =
        new("MedStock.Web.Resources.SharedResource", typeof(SharedResource).Assembly);

    public static string Get(string key, CultureInfo culture) => Rm.GetString(key, culture) ?? key;

    public static string Format(string key, CultureInfo culture, params object?[] args) =>
        string.Format(culture, Get(key, culture), args);

    public static string Unit(MedicationType t, decimal count, CultureInfo c) =>
        Get($"Unit_{t}_{(count == 1 ? "One" : "Other")}", c);

    public static string Package(MedicationType t, decimal count, CultureInfo c) =>
        Get($"Package_{t}_{(count == 1 ? "One" : "Other")}", c);

    public static string ShortDate(DateOnly d, CultureInfo c) => d.ToString("ddd d MMM", c);
    public static string LongDate(DateOnly d, CultureInfo c) => d.ToString("d MMM yyyy", c);

    /// <summary>"1 box + 20 doses", "2 boxes", "20 doses".</summary>
    public static string Quantity(MedicationType t, StockResult r, CultureInfo c)
    {
        string units = $"{r.RestUnits} {Unit(t, r.RestUnits, c)}";
        if (r.Packages == 0) return units;
        string pk = $"{r.Packages} {Package(t, r.Packages, c)}";
        return r.RestUnits == 0 ? pk : $"{pk} + {units}";
    }

    public static string UnitsCount(MedicationType t, decimal remaining, CultureInfo c)
    {
        var n = Math.Floor(remaining);
        return $"{n.ToString("0", c)} {Unit(t, n, c)}";
    }

    /// <summary>Notification body, e.g. "Nasalix (Husband): 12 days left".</summary>
    public static string NotificationMessage(string medication, string profile, MedicationType type, UsageMode mode, StockResult r, CultureInfo c)
    {
        string head = $"{medication} ({profile})";
        if (r.State == StockState.Empty) return Format("Notify_Empty", c, head);
        if (mode == UsageMode.AsNeeded) return Format("Notify_AsNeeded", c, head, UnitsCount(type, r.Remaining, c));
        if (r.State == StockState.LastDay) return Format("Notify_LastDay", c, head);
        if (mode == UsageMode.Weekly && r.LastDoseDate is { } ld)
            return Format("Notify_Weekly", c, head, UnitsCount(type, r.Remaining, c), ShortDate(ld, c));
        return Format("Notify_Days", c, head, r.DaysRemaining);
    }

    /// <summary>Discreet notification body without the medication name, e.g. "Husband: a medication has 12 days left".</summary>
    public static string DiscreetMessage(string profile, UsageMode mode, StockResult r, CultureInfo c)
    {
        if (r.State == StockState.Empty) return Format("Notify_Discreet_Empty", c, profile);
        if (mode == UsageMode.AsNeeded) return Format("Notify_Discreet_Low", c, profile);
        if (r.State == StockState.LastDay) return Format("Notify_Discreet_LastDay", c, profile);
        return r.DaysRemaining == 1 ? Format("Notify_Discreet_Day", c, profile) : Format("Notify_Discreet_Days", c, profile, r.DaysRemaining);
    }
}
