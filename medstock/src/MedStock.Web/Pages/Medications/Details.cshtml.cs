using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Medications;

/// <summary>Detail with the stock actions (§3.4). Every action posts, redirects back (PRG) and shows a short confirmation.</summary>
public class DetailsModel(MedStockDb db, IClock clock, AppPaths paths, MedicationService service, IStringLocalizer<SharedResource> l)
    : PageModel
{
    public MedicationRow Row { get; private set; } = null!;
    public long PhotoVersion { get; private set; }
    /// <summary>Usage today with the current settings: the recount checkbox is only shown when this is &gt; 0.</summary>
    public decimal UsageToday { get; private set; }
    public bool DefaultTakenToday => MedicationService.DefaultTakenToday(clock.Now);
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

    public Task<IActionResult> OnPostRecountAsync(int id, int expectedSnapshotId, decimal? openUnits, int? sealedPackages, bool takenToday) =>
        openUnits is < 0 || sealedPackages is < 0
            ? Invalid(id, l["Val_NonNegative"])
            : Run(id, () => service.RecountAsync(id, expectedSnapshotId, openUnits ?? 0, sealedPackages ?? 0, takenToday), "recounted");

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
        UsageToday = MedicationService.UsageOn(row.Medication, clock.Today);
        return true;
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
            _ => null,
        })?.Value;
    }
}
