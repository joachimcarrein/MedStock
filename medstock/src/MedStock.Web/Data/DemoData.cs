using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Data;

/// <summary>Fictional demo data for screenshots and local testing (<c>--demo</c>). Only seeds an empty database.</summary>
public static class DemoData
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MedStockDb>();
        var today = scope.ServiceProvider.GetRequiredService<IClock>().Today;
        if (await db.Profiles.AnyAsync()) return;

        var husband = new Profile { Name = "Husband", Color = "#1e88e5", SortOrder = 0 };
        var wife = new Profile { Name = "Wife", Color = "#d81b60", SortOrder = 1 };
        db.Profiles.AddRange(husband, wife);
        await db.SaveChangesAsync();

        void Add(Profile p, string name, string? strength, MedicationType type, decimal upp, UsageMode mode,
            decimal perDay, decimal[]? weekly, decimal units, int daysAgo, decimal min = 0)
        {
            var m = new Medication
            {
                ProfileId = p.Id, Name = name, Strength = strength, Type = type, UnitsPerPackage = upp, UsageMode = mode,
                PerDay = perDay, Weekly = weekly ?? new decimal[7], MinStockUnits = min, CreatedAt = DateTime.UtcNow,
            };
            m.Snapshots.Add(new Snapshot { Date = today.AddDays(-daysAgo), TotalUnits = units, Reason = SnapshotReason.Initial, CreatedAt = DateTime.UtcNow, CreatedByUserId = "demo" });
            db.Medications.Add(m);
        }

        Add(husband, "Nasal spray", "50 µg", MedicationType.NasalSpray, 140, UsageMode.PerDay, 4, null, 240, 10);
        Add(husband, "Weekly injection", "0.5 mg", MedicationType.Injection, 4, UsageMode.Weekly, 0, [0, 0, 0, 0, 0, 1, 0], 3, 2);
        Add(husband, "Vitamin D", "25 µg", MedicationType.Capsule, 90, UsageMode.PerDay, 1, null, 200, 5);
        Add(wife, "Blood pressure", "5 mg", MedicationType.Pill, 28, UsageMode.PerDay, 1, null, 20, 3);
        Add(wife, "Paracetamol", "500 mg", MedicationType.Pill, 20, UsageMode.AsNeeded, 0, null, 6, 0, min: 10);
        Add(wife, "Inhaler", null, MedicationType.Inhaler, 200, UsageMode.PerDay, 2, null, 30, 0);
        await db.SaveChangesAsync();
    }
}
