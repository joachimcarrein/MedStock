using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Medications;

/// <summary>Detail with the stock actions (§3.4). Every action posts, redirects back (PRG) and shows a short confirmation.</summary>
public class DetailsModel(MedStockDb db, IClock clock, AppPaths paths, MedicationService service, MedStock.Web.Middleware.CurrentUser user,
    IStringLocalizer<SharedResource> l)
    : PageModel
{
    public MedicationRow Row { get; private set; } = null!;
    public long PhotoVersion { get; private set; }
    /// <summary>Today's doses still to come: a recount subtracts them (shown before saving).</summary>
    public List<DoseMoment> PendingToday { get; private set; } = [];
    public TimeOnly UntimedTakenFrom { get; private set; }
    /// <summary>The latest changes (newest first) with the change in units and who made them.</summary>
    public List<HistoryEntry> History { get; private set; } = [];
    /// <summary>The latest snapshot can be taken back (purchase, intake or recount, with an earlier one before it).</summary>
    public bool CanUndo => History.Count > 1 && MedicationService.IsUndoable(History[0].Snapshot.Reason);

    /// <summary>The current user's own reminder choice for this medication: null = follow the profile choice in Settings.</summary>
    public bool? ReminderOverride { get; private set; }
    /// <summary>Whether the current user gets reminders for this profile (Settings).</summary>
    public bool ReminderProfileOn { get; private set; }
    /// <summary>Reminders need a time: a daily moment with a time or a weekly time.</summary>
    public bool HasIntakeTime => Row.Medication.UsageMode switch
    {
        UsageMode.PerDay => DoseSchedule.MomentsOf(Row.Medication).Any(x => x.Time is not null),
        UsageMode.Weekly => Row.Medication.WeeklyTime is not null,
        _ => false,
    };
    public string? Error { get; private set; }

    /// <summary>Confirmation after a redirect: "added", "taken", "recounted", "paused", "resumed".</summary>
    [BindProperty(SupportsGet = true)] public string? Done { get; set; }
    [BindProperty(SupportsGet = true)] public int? N { get; set; }

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostPurchaseAsync(int id, int? packages) =>
        packages is not >= 1 or > 1000
            ? Invalid(id, l["Val_AtLeastOne"])
            : Run(id, () => service.PurchaseAsync(id, packages.Value), "added", packages);

    public Task<IActionResult> OnPostIntakeAsync(int id, int? units) =>
        units is not >= 1 or > 100000
            ? Invalid(id, l["Val_AtLeastOne"])
            : Run(id, () => service.IntakeAsync(id, units.Value), "taken", units);

    public Task<IActionResult> OnPostRecountAsync(int id, int expectedSnapshotId, decimal? openUnits, int? sealedPackages) =>
        openUnits is < 0 || sealedPackages is < 0
            ? Invalid(id, l["Val_NonNegative"])
            : Run(id, () => service.RecountAsync(id, expectedSnapshotId, openUnits ?? 0, sealedPackages ?? 0), "recounted");

    /// <summary>The current user's reminder choice for this medication: "auto" (profile choice), "on" or "off".</summary>
    public async Task<IActionResult> OnPostReminderAsync(int id, string? reminder)
    {
        if (!await db.Medications.AnyAsync(m => m.Id == id && !m.Archived)) return NotFound();
        var existing = await db.MedicationReminders.FirstOrDefaultAsync(r => r.UserId == user.Id && r.MedicationId == id);
        if (existing is not null) db.MedicationReminders.Remove(existing);
        if (reminder is "on" or "off")
            db.MedicationReminders.Add(new MedicationReminder { UserId = user.Id, MedicationId = id, Enabled = reminder == "on" });
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, done = "reminder" });
    }

    public async Task<IActionResult> OnPostUndoAsync(int id, int snapshotId)
    {
        try { await service.UndoAsync(id, snapshotId); }
        catch (InvalidOperationException) { return await Invalid(id, l["Undo_Nothing"]); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (StaleSnapshotException)
        {
            if (!await LoadAsync(id)) return NotFound();
            Error = l["Conflict"];
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        return RedirectToPage(new { id, done = "undone" });
    }

    public Task<IActionResult> OnPostPauseAsync(int id, int expectedSnapshotId, bool paused) =>
        Run(id, () => service.SetPausedAsync(id, expectedSnapshotId, paused), paused ? "paused" : "resumed");

    /// <summary>Links (or with an empty value removes) the barcode; stored as the product number (see <see cref="Barcodes"/>).</summary>
    public async Task<IActionResult> OnPostBarcodeAsync(int id, string? barcode)
    {
        var m = await db.Medications.FindAsync(id);
        if (m is null) return NotFound();
        m.Barcode = Barcodes.Normalize(barcode);
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, done = m.Barcode is null ? "barcode-removed" : "barcode" });
    }

    public async Task<IActionResult> OnPostArchiveAsync(int id)
    {
        try { await service.SetArchivedAsync(id, true); }
        catch (KeyNotFoundException) { return NotFound(); }
        return Redirect(Url.Content("~/"));
    }

    private async Task<IActionResult> Run(int id, Func<Task> action, string done, int? n = null)
    {
        try { await action(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (StaleSnapshotException)
        {
            if (!await LoadAsync(id)) return NotFound();
            Error = l["Conflict"];
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        return RedirectToPage(new { id, done, n });
    }

    private async Task<IActionResult> Invalid(int id, string message)
    {
        if (!await LoadAsync(id)) return NotFound();
        Error = message;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    private async Task<bool> LoadAsync(int id)
    {
        Done = Request.Method == "GET" ? Done : null;
        var row = (await MedicationQueries.LoadAsync(db, clock.Today, id: id)).SingleOrDefault();
        if (row is null) return false;
        Row = row;
        PhotoVersion = PhotoModel.Version(paths, id);
        UntimedTakenFrom = await db.GlobalSettings.AsNoTracking().Select(g => g.UntimedTakenFrom).SingleAsync();
        PendingToday = DoseSchedule.PendingToday(row.Medication, clock.Now, UntimedTakenFrom);
        History = await LoadHistoryAsync(row.Medication);
        ReminderOverride = await db.MedicationReminders.AsNoTracking().Where(r => r.UserId == user.Id && r.MedicationId == id)
            .Select(r => (bool?)r.Enabled).FirstOrDefaultAsync();
        ReminderProfileOn = await db.UserSettings.AsNoTracking().Where(s => s.UserId == user.Id)
            .Select(s => s.ReminderProfileIds).FirstOrDefaultAsync() is { } ids && ids.Contains(row.Medication.ProfileId);
        return true;
    }

    private async Task<List<HistoryEntry>> LoadHistoryAsync(Medication m)
    {
        const int shown = 6;
        var snaps = await db.Snapshots.AsNoTracking().Where(s => s.MedicationId == m.Id).OrderByDescending(s => s.Id).Take(shown + 1).ToListAsync();
        var userIds = snaps.Select(s => s.CreatedByUserId).Where(x => x is not null).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var list = new List<HistoryEntry>();
        for (var i = 0; i < Math.Min(shown, snaps.Count); i++)
        {
            var s = snaps[i];
            decimal? delta = null;
            if (i + 1 < snaps.Count && s.Reason is SnapshotReason.Purchase or SnapshotReason.Intake)
                delta = s.TotalUnits - StockCalculator.Remaining(StockInput.From(m, snaps[i + 1]), s.Date);
            var by = s.CreatedByUserId switch
            {
                null => null,
                NotificationActionHandler.FallbackUserId => l["History_ViaNotification"].Value,
                var uid => names.TryGetValue(uid, out var n) ? n : null,
            };
            list.Add(new HistoryEntry(s, delta, by));
        }
        return list;
    }

    public string? DoneMessage(System.Globalization.CultureInfo c)
    {
        var t = Row.Medication.Type;
        var n = N ?? 1;
        return (Done switch
        {
            "added" => l["Flash_Added", n, Texts.Package(t, n, c)],
            "taken" => l["Flash_Taken", n, Texts.Unit(t, n, c)],
            "recounted" => l["Flash_Recounted"],
            "paused" => l["Badge_Paused"],
            "resumed" => l["Flash_Resumed"],
            "scanned" => l["Flash_Scanned", Texts.Package(t, 1, c)],
            "linked" => l["Flash_Linked"],
            "barcode" => l["Flash_Barcode"],
            "barcode-removed" => l["Flash_BarcodeRemoved"],
            "reminder" => l["Flash_Reminder"],
            "undone" => l["Flash_Undone"],
            _ => null,
        })?.Value;
    }
}

/// <summary>One line of "latest changes": the snapshot, units added or taken (purchase, intake) and who did it.</summary>
public sealed record HistoryEntry(Snapshot Snapshot, decimal? Delta, string? By);
