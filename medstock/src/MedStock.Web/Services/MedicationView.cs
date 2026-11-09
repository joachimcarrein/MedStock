using System.Globalization;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>A medication with its profile, current snapshot and calculated stock, ready for display.</summary>
public sealed record MedicationRow(Medication Medication, Profile Profile, Snapshot Snapshot, StockResult Stock)
{
    public string ColorClass => Stock.State switch
    {
        StockState.Empty or StockState.LastDay or StockState.Critical or StockState.AsNeededLow => "red",
        StockState.Warning => "orange",
        StockState.Ok or StockState.CourseCovered => "green",
        _ => "grey",
    };

    public string Badge(CultureInfo c) => Stock.State switch
    {
        StockState.Empty => Texts.Get("Badge_Empty", c),
        StockState.LastDay => Texts.Get("Badge_LastDay", c),
        StockState.Paused => Texts.Get("Badge_Paused", c),
        StockState.CourseCovered => Texts.Get("Badge_CourseCovered", c),
        StockState.CourseEnded => Texts.Get("Badge_CourseEnded", c),
        StockState.AsNeededLow or StockState.AsNeededOk =>
            Texts.Format("Badge_AsNeeded", c, Texts.UnitsCount(Medication.Type, Stock.Remaining, c)),
        _ when Stock.CapReached => Texts.Get("Badge_Cap", c),
        _ when Stock.DaysRemaining == 1 => Texts.Get("Badge_Days_One", c),
        _ => Texts.Format("Badge_Days_Other", c, Stock.DaysRemaining),
    };

    public string? LastDose(CultureInfo c) =>
        Stock.LastDoseDate is { } d && !Stock.CapReached ? Texts.Format("LastDose", c, Texts.ShortDate(d, c)) : null;
}

public static class MedicationQueries
{
    /// <summary>Loads medication with the current snapshot and calculates stock in memory (never sort on decimals in SQL).</summary>
    public static async Task<List<MedicationRow>> LoadAsync(MedStockDb db, DateOnly today, bool archived = false, int? id = null)
    {
        var global = await db.GlobalSettings.AsNoTracking().SingleAsync();
        var meds = await db.Medications.AsNoTracking().Include(m => m.Profile)
            .Where(m => m.Archived == archived && (id == null || m.Id == id)).ToListAsync();
        var ids = meds.Select(m => m.Id).ToList();
        var latestIds = await db.Snapshots.Where(s => ids.Contains(s.MedicationId))
            .GroupBy(s => s.MedicationId).Select(g => g.Max(s => s.Id)).ToListAsync();
        var snapshots = await db.Snapshots.AsNoTracking().Where(s => latestIds.Contains(s.Id)).ToDictionaryAsync(s => s.MedicationId);

        return meds.Where(m => snapshots.ContainsKey(m.Id))
            .Select(m =>
            {
                var s = snapshots[m.Id];
                var r = StockCalculator.Calculate(StockInput.From(m, s), today, m.AlertDays ?? global.DefaultAlertDays);
                return new MedicationRow(m, m.Profile!, s, r);
            })
            .OrderBy(r => r.Stock.StateRank).ThenBy(r => r.Stock.SortDays)
            .ThenBy(r => r.Medication.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
