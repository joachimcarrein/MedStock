using System.Globalization;
using System.Text.Json;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Pages.Settings;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>Outcome of an import; <see cref="Error"/> is a resource key when the file could not be used at all.</summary>
public sealed record ImportResult(int ProfilesCreated, int MedicationsImported, int Skipped, int Invalid, bool SettingsApplied, string? Error = null);

/// <summary>
/// Imports a JSON export (format 1) by adding: profiles are matched by name (created when missing), medication that already
/// exists in the same profile (same name and strength, archived included) is skipped. Nothing is ever overwritten or deleted.
/// The exported stock becomes the first count. Logs counts only.
/// </summary>
public sealed class ImportService(MedStockDb db, IClock clock, SensorSignal sensors, ILogger<ImportService> log)
{
    public const long MaxBytes = 2_000_000;

    public async Task<ImportResult> ImportAsync(Stream json, bool applySettings, string? userId, CancellationToken ct = default)
    {
        ExportDocument? doc;
        try { doc = await JsonSerializer.DeserializeAsync<ExportDocument>(json, ExportModel.Json, ct); }
        catch (JsonException) { return Failed("Import_NotAnExport"); }
        if (doc?.Profiles is null || doc.Medications is null) return Failed("Import_NotAnExport");
        if (doc.Format != ExportModel.FormatVersion) return Failed("Import_UnknownFormat");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profiles = await db.Profiles.ToListAsync(ct);
        var byExportId = new Dictionary<int, Profile>();
        int created = 0, imported = 0, skipped = 0, invalid = 0;
        var nextOrder = profiles.Count == 0 ? 0 : profiles.Max(p => p.SortOrder) + 1;

        foreach (var ep in doc.Profiles.OrderBy(p => p.SortOrder))
        {
            var name = ep.Name?.Trim() ?? "";
            if (name.Length is 0 or > 50) { invalid++; continue; }
            var p = profiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (p is null)
            {
                p = new Profile
                {
                    Name = name,
                    Color = ProfilesModel.Palette.Contains(ep.Color) ? ep.Color : ProfilesModel.Palette[profiles.Count % ProfilesModel.Palette.Length],
                    SortOrder = nextOrder++,
                    CoverageDays = ep.CoverageDays is >= 0 and <= 3650 ? ep.CoverageDays : null,
                };
                db.Profiles.Add(p);
                profiles.Add(p);
                created++;
            }
            byExportId[ep.Id] = p;
        }

        var existing = await db.Medications.Select(m => new { m.ProfileId, m.Name, m.Strength }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var em in doc.Medications)
        {
            if (!byExportId.TryGetValue(em.ProfileId, out var profile) || !TryMap(em, clock.Today, out var med, out var stock))
            {
                invalid++;
                continue;
            }
            bool duplicate = existing.Any(x => x.ProfileId == profile.Id
                    && string.Equals(x.Name, med.Name, StringComparison.CurrentCultureIgnoreCase)
                    && string.Equals(x.Strength ?? "", med.Strength ?? "", StringComparison.CurrentCultureIgnoreCase));
            if (duplicate) { skipped++; continue; }
            med.Profile = profile;
            med.CreatedAt = now;
            med.Snapshots = [new Snapshot { Date = stock.Date, TotalUnits = stock.Units, Reason = SnapshotReason.Initial, CreatedAt = now, CreatedByUserId = userId }];
            db.Medications.Add(med);
            imported++;
        }

        bool settingsApplied = false;
        if (applySettings && doc.Settings is { } s && s.CoverageDays is >= 0 and <= 3650 && s.DefaultAlertDays is >= 0 and <= 3650
            && TimeOnly.TryParseExact(s.NotificationTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var g = await db.GlobalSettings.SingleAsync(ct);
            g.CoverageDays = s.CoverageDays;
            g.DefaultAlertDays = s.DefaultAlertDays;
            g.NotificationTime = time;
            settingsApplied = true;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        sensors.Request();
        log.LogInformation("Import: {Profiles} profiles created, {Imported} medications imported, {Skipped} skipped, {Invalid} invalid",
            created, imported, skipped, invalid);
        return new ImportResult(created, imported, skipped, invalid, settingsApplied);
    }

    private static ImportResult Failed(string key) => new(0, 0, 0, 0, false, key);

    private static bool TryMap(ExportMedication e, DateOnly today, out Medication m, out (DateOnly Date, decimal Units) stock)
    {
        m = null!;
        stock = default;
        var name = e.Name?.Trim() ?? "";
        var strength = string.IsNullOrWhiteSpace(e.Strength) ? null : e.Strength.Trim();
        if (name.Length is 0 or > 100 || strength?.Length > 50) return false;
        if (!Enum.TryParse<MedicationType>(e.Type, out var type) || !Enum.IsDefined(type)) return false;
        if (!Enum.TryParse<UsageMode>(e.UsageMode, out var mode) || !Enum.IsDefined(mode)) return false;
        if (e.UnitsPerPackage <= 0 || e.PerDay < 0 || e.MinStockUnits < 0) return false;
        if (e.Weekly is not { Length: 7 } || e.Weekly.Any(w => w < 0)) return false;
        if (e.AlertDays is < 0 or > 3650 || e.CoverageDays is < 0 or > 3650) return false;
        if (e.Barcode?.Length > 64) return false;
        if (e.TakeAlongUnits is <= 0 || e.TakeAlongUnits > e.UnitsPerPackage) return false;
        DateOnly? endDate = null;
        if (!string.IsNullOrEmpty(e.EndDate))
        {
            if (!DateOnly.TryParseExact(e.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return false;
            endDate = end;
        }

        if (e.Stock is { } s)
        {
            if (!DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || s.TotalUnits < 0)
                return false;
            stock = (d, s.TotalUnits);
        }
        else stock = (today, 0m);

        m = new Medication
        {
            Name = name, Strength = strength, Type = type, UnitsPerPackage = e.UnitsPerPackage, UsageMode = mode, PerDay = e.PerDay,
            Weekly = [.. e.Weekly], MinStockUnits = e.MinStockUnits, AlertDays = e.AlertDays, CoverageDays = e.CoverageDays,
            NotificationsEnabled = e.NotificationsEnabled, Paused = e.Paused, Archived = e.Archived,
            Barcode = string.IsNullOrWhiteSpace(e.Barcode) ? null : e.Barcode.Trim(),
            TakeAlongUnits = e.TakeAlongUnits,
            EndDate = endDate,
        };
        return true;
    }
}
