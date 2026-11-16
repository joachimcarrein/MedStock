using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>Thrown when an absolute action (recount, usage change, pause) was based on an outdated snapshot.</summary>
public sealed class StaleSnapshotException() : Exception("The medication was changed by someone else.");

/// <summary>Thrown when an uploaded photo is not a JPEG or PNG.</summary>
public sealed class InvalidPhotoException() : Exception("Only JPEG or PNG images are accepted.");

/// <summary>
/// Every change to stock goes through here: one process-wide lock, the current remaining stock is recalculated
/// inside the lock and a new snapshot is written (§3.1).
/// </summary>
public sealed class MedicationService(MedStockDb db, IClock clock, CurrentUser user, AppPaths paths, SensorSignal sensors,
    ILogger<MedicationService> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public const long MaxPhotoBytes = 8_000_000;

    /// <summary>Usage on <paramref name="day"/> with the medication's current settings (0 when paused or as needed).</summary>
    public static decimal UsageOn(Medication m, DateOnly day) =>
        StockCalculator.Usage(StockInput.From(m, new Snapshot { Date = day, TotalUnits = 0 }), day);

    /// <summary>Counted stock (open + sealed packages) minus the doses that still follow today (§3.1: end of the day).</summary>
    public static decimal CountedUnits(Medication m, decimal openUnits, int sealedPackages, DateTime now, TimeOnly untimedFrom)
    {
        var total = openUnits + sealedPackages * m.UnitsPerPackage;
        return Math.Max(0, total - DoseSchedule.PendingToday(m, now, untimedFrom).Sum(x => x.Amount));
    }

    private Task<TimeOnly> UntimedFromAsync() =>
        db.GlobalSettings.AsNoTracking().Select(g => g.UntimedTakenFrom).SingleAsync();

    public Task<Snapshot?> LatestSnapshotAsync(int medicationId) =>
        db.Snapshots.AsNoTracking().Where(s => s.MedicationId == medicationId).OrderByDescending(s => s.Id).FirstOrDefaultAsync();

    public async Task<int> CreateAsync(Medication m, decimal openUnits, int sealedPackages)
    {
        var untimedFrom = await UntimedFromAsync();
        await Gate.WaitAsync();
        try
        {
            var now = clock.Now;
            m.CreatedAt = DateTime.UtcNow;
            m.Snapshots.Add(NewSnapshot(0, DateOnly.FromDateTime(now), CountedUnits(m, openUnits, sealedPackages, now, untimedFrom), SnapshotReason.Initial));
            db.Medications.Add(m);
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId} created in profile {ProfileId}", m.Id, m.ProfileId);
            return m.Id;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Saves an edit. When the usage (mode, per day, weekly, minimum) changes, a DoseChange snapshot with the
    /// remaining stock under the OLD usage is written first, in the same SaveChanges (one transaction); that path
    /// requires <paramref name="expectedSnapshotId"/> to still be the latest snapshot.
    /// </summary>
    public async Task UpdateAsync(int id, int expectedSnapshotId, Action<Medication> apply)
    {
        await Gate.WaitAsync();
        try
        {
            var (m, latest) = await LoadAsync(id);
            var before = StockInput.From(m, latest) with { Weekly = (decimal[])m.Weekly.Clone() };
            apply(m);
            var usageChanged = before.Mode != m.UsageMode || before.PerDay != m.PerDay || before.MinStockUnits != m.MinStockUnits
                               || !before.Weekly.SequenceEqual(m.Weekly) || before.EndDate != m.EndDate
                               || before.Every != m.Every || before.EveryFrom != m.EveryFrom; // times alone change no usage
            if (usageChanged)
            {
                if (latest.Id != expectedSnapshotId) throw new StaleSnapshotException();
                db.Snapshots.Add(NewSnapshot(m.Id, clock.Today, StockCalculator.Remaining(before, clock.Today), SnapshotReason.DoseChange));
            }
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId} updated (usage changed: {UsageChanged})", id, usageChanged);
        }
        finally { Gate.Release(); }
    }

    public Task PurchaseAsync(int id, int packages) => RelativeAsync(id, SnapshotReason.Purchase,
        (m, remaining) => remaining + packages * m.UnitsPerPackage, packages);

    public Task IntakeAsync(int id, int units) => RelativeAsync(id, SnapshotReason.Intake,
        (_, remaining) => Math.Max(0, remaining - units), units);

    public async Task RecountAsync(int id, int expectedSnapshotId, decimal openUnits, int sealedPackages)
    {
        var untimedFrom = await UntimedFromAsync();
        await Gate.WaitAsync();
        try
        {
            var (m, latest) = await LoadAsync(id);
            if (latest.Id != expectedSnapshotId) throw new StaleSnapshotException();
            var now = clock.Now;
            db.Snapshots.Add(NewSnapshot(m.Id, DateOnly.FromDateTime(now), CountedUnits(m, openUnits, sealedPackages, now, untimedFrom), SnapshotReason.Recount));
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId} recounted", id);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Snapshot reasons that "undo" can take back: removing the snapshot restores the stock before it.</summary>
    public static bool IsUndoable(SnapshotReason r) => r is SnapshotReason.Purchase or SnapshotReason.Intake or SnapshotReason.Recount;

    /// <summary>
    /// Takes back the latest purchase, intake or recount by removing its snapshot: the stock is calculated from the previous
    /// one again. Only the latest snapshot, and only when it is still <paramref name="snapshotId"/> (else stale).
    /// </summary>
    public async Task<SnapshotReason> UndoAsync(int id, int snapshotId)
    {
        await Gate.WaitAsync();
        try
        {
            var latest = await db.Snapshots.Where(s => s.MedicationId == id).OrderByDescending(s => s.Id).Take(2).ToListAsync();
            if (latest.Count == 0) throw new KeyNotFoundException();
            if (latest[0].Id != snapshotId) throw new StaleSnapshotException();
            if (latest.Count < 2 || !IsUndoable(latest[0].Reason)) throw new InvalidOperationException("Nothing to undo.");
            var reason = latest[0].Reason;
            db.Snapshots.Remove(latest[0]);
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId}: {Reason} undone", id, reason);
            return reason;
        }
        finally { Gate.Release(); }
    }

    public async Task SetPausedAsync(int id, int expectedSnapshotId, bool paused)
    {
        await Gate.WaitAsync();
        try
        {
            var (m, latest) = await LoadAsync(id);
            if (latest.Id != expectedSnapshotId) throw new StaleSnapshotException();
            if (m.Paused == paused) return;
            db.Snapshots.Add(NewSnapshot(m.Id, clock.Today, StockCalculator.Remaining(StockInput.From(m, latest), clock.Today),
                paused ? SnapshotReason.Pause : SnapshotReason.Resume));
            m.Paused = paused;
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId} paused: {Paused}", id, paused);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Archive or restore; no snapshot (days are simply calculated from the last snapshot again).</summary>
    public async Task SetArchivedAsync(int id, bool archived)
    {
        var m = await db.Medications.FindAsync(id) ?? throw new KeyNotFoundException();
        m.Archived = archived;
        await db.SaveChangesAsync();
        sensors.Request();
        log.LogInformation("Medication {MedicationId} archived: {Archived}", id, archived);
    }

    /// <summary>Deletes an archived medication with its snapshots, notification log rows and photo.</summary>
    public async Task DeletePermanentlyAsync(int id)
    {
        await Gate.WaitAsync();
        try
        {
            var m = await db.Medications.FirstOrDefaultAsync(x => x.Id == id && x.Archived) ?? throw new KeyNotFoundException();
            db.NotificationLogs.RemoveRange(db.NotificationLogs.Where(n => n.MedicationId == id));
            db.Medications.Remove(m); // snapshots cascade
            await db.SaveChangesAsync();
            sensors.Request();
            File.Delete(paths.PhotoPath(id));
            log.LogInformation("Medication {MedicationId} deleted permanently", id);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Stores a photo as /data/photos/&lt;id&gt;.jpg (atomic replace). Only JPEG and PNG signatures are accepted.</summary>
    public async Task SavePhotoAsync(int id, Stream content)
    {
        var m = await db.Medications.FindAsync(id) ?? throw new KeyNotFoundException();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var isJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        if (!isJpeg && !isPng) throw new InvalidPhotoException();

        Directory.CreateDirectory(paths.PhotosDir);
        var target = paths.PhotoPath(id);
        var tmp = target + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, target, overwrite: true);
        m.HasPhoto = true;
        await db.SaveChangesAsync();
        sensors.Request();
        log.LogInformation("Photo stored for medication {MedicationId} ({Bytes} bytes)", id, bytes.Length);
    }

    public async Task DeletePhotoAsync(int id)
    {
        var m = await db.Medications.FindAsync(id) ?? throw new KeyNotFoundException();
        File.Delete(paths.PhotoPath(id));
        m.HasPhoto = false;
        await db.SaveChangesAsync();
        sensors.Request();
    }

    private async Task RelativeAsync(int id, SnapshotReason reason, Func<Medication, decimal, decimal> next, int amount)
    {
        await Gate.WaitAsync();
        try
        {
            var (m, latest) = await LoadAsync(id);
            var remaining = StockCalculator.Remaining(StockInput.From(m, latest), clock.Today);
            db.Snapshots.Add(NewSnapshot(m.Id, clock.Today, next(m, remaining), reason));
            await db.SaveChangesAsync();
            sensors.Request();
            log.LogInformation("Medication {MedicationId}: {Reason} {Amount}", id, reason, amount);
        }
        finally { Gate.Release(); }
    }

    private async Task<(Medication, Snapshot)> LoadAsync(int id)
    {
        var m = await db.Medications.FirstOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.MedicationId == id).OrderByDescending(s => s.Id).FirstAsync();
        return (m, latest);
    }

    private Snapshot NewSnapshot(int medicationId, DateOnly date, decimal units, SnapshotReason reason) => new()
    {
        MedicationId = medicationId,
        Date = date,
        TotalUnits = units,
        Reason = reason,
        CreatedAt = DateTime.UtcNow,
        CreatedByUserId = user.Id,
    };
}
