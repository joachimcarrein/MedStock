using MedStock.Web.Domain;
using MedStock.Web.Services;

namespace MedStock.Tests;

internal static class T
{
    public static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    public static StockInput PerDay(decimal perDay, string date, decimal units, decimal upp = 140, bool paused = false) =>
        new(UsageMode.PerDay, perDay, new decimal[7], paused, 0, upp, D(date), units);

    public static StockInput Weekly(decimal[] weekly, string date, decimal units, decimal upp = 4, bool paused = false) =>
        new(UsageMode.Weekly, 0, weekly, paused, 0, upp, D(date), units);

    public static StockInput AsNeeded(decimal min, string date, decimal units, decimal upp = 20) =>
        new(UsageMode.AsNeeded, 0, new decimal[7], false, min, upp, D(date), units);

    public static readonly decimal[] Saturday = [0, 0, 0, 0, 0, 1, 0];
    public static readonly decimal[] Sunday = [0, 0, 0, 0, 0, 0, 1];
}
