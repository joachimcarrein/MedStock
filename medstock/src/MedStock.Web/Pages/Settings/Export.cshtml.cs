using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages.Settings;

/// <summary>
/// JSON export of profiles, medication (incl. archived) with their current snapshot, and the global settings, for
/// portability. Photos and per-user notification settings are not included. Read-only (GET).
/// </summary>
public class ExportModel(MedStockDb db, IClock clock, ILogger<ExportModel> log) : PageModel
{
    public const int FormatVersion = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep µ, é readable
    };

    public async Task<IActionResult> OnGetAsync()
    {
        var export = await BuildAsync(db, clock.Now);
        log.LogInformation("Export downloaded ({Medications} medications)", export.Medications.Count);
        var name = $"medstock-export-{clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";
        Response.Headers.CacheControl = "no-store";
        return File(JsonSerializer.SerializeToUtf8Bytes(export, Json), "application/json; charset=utf-8", name);
    }

    public static async Task<ExportDocument> BuildAsync(MedStockDb db, DateTime now)
    {
        var global = await db.GlobalSettings.AsNoTracking().SingleAsync();
        var profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        var meds = await db.Medications.AsNoTracking().OrderBy(m => m.ProfileId).ThenBy(m => m.Name).ToListAsync();
        var ids = meds.Select(m => m.Id).ToList();
        var latestIds = await db.Snapshots.Where(s => ids.Contains(s.MedicationId)).GroupBy(s => s.MedicationId)
            .Select(g => g.Max(s => s.Id)).ToListAsync();
        var snapshots = await db.Snapshots.AsNoTracking().Where(s => latestIds.Contains(s.Id)).ToDictionaryAsync(s => s.MedicationId);

        return new ExportDocument(
            FormatVersion, DbInitializer.AppVersion, now,
            new ExportGlobal(global.CoverageDays, global.DefaultAlertDays, global.NotificationTime.ToString("HH:mm", CultureInfo.InvariantCulture)),
            profiles.Select(p => new ExportProfile(p.Id, p.Name, p.Color, p.SortOrder, p.CoverageDays)).ToList(),
            meds.Select(m => new ExportMedication(
                m.Id, m.ProfileId, m.Name, m.Strength, m.Type.ToString(), m.UnitsPerPackage, m.UsageMode.ToString(), m.PerDay,
                m.Weekly, m.MinStockUnits, m.AlertDays, m.CoverageDays, m.NotificationsEnabled, m.Paused, m.Archived,
                snapshots.TryGetValue(m.Id, out var s)
                    ? new ExportStock(s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.TotalUnits)
                    : null,
                m.Barcode, m.TakeAlongUnits)).ToList());
    }
}

public sealed record ExportDocument(int Format, string AppVersion, DateTime ExportedAt, ExportGlobal Settings,
    List<ExportProfile> Profiles, List<ExportMedication> Medications);

public sealed record ExportGlobal(int CoverageDays, int DefaultAlertDays, string NotificationTime);

public sealed record ExportProfile(int Id, string Name, string Color, int SortOrder, int? CoverageDays);

/// <summary>Weekly: units per weekday, Monday first. Stock: total units at the END of that date.</summary>
public sealed record ExportMedication(int Id, int ProfileId, string Name, string? Strength, string Type, decimal UnitsPerPackage,
    string UsageMode, decimal PerDay, decimal[] Weekly, decimal MinStockUnits, int? AlertDays, int? CoverageDays,
    bool NotificationsEnabled, bool Paused, bool Archived, ExportStock? Stock, string? Barcode = null,
    decimal? TakeAlongUnits = null);

public sealed record ExportStock(string Date, decimal TotalUnits);
