using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages.Medications;

/// <summary>
/// Result of a scan on the overview: one match opens its detail (ready for "+1"), several matches (the same product in
/// two profiles) let you choose, an unknown code can be linked to a medication.
/// </summary>
public class ScanModel(MedStockDb db, IClock clock, ILogger<ScanModel> log) : PageModel
{
    public string? Code { get; private set; }
    public List<MedicationRow> Matches { get; private set; } = [];
    public List<MedicationRow> All { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? code)
    {
        Code = Barcodes.Normalize(code);
        if (Code is null) return Redirect(Url.Content("~/"));
        var rows = await MedicationQueries.LoadAsync(db, clock.Today);
        Matches = rows.Where(r => r.Medication.Barcode == Code).ToList();
        if (Matches.Count == 1) return RedirectToPage("/Medications/Details", new { id = Matches[0].Medication.Id, done = "scanned" });
        All = rows.OrderBy(r => r.Profile.SortOrder).ThenBy(r => r.Medication.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Page();
    }

    /// <summary>Links the scanned code to a medication, then opens it.</summary>
    public async Task<IActionResult> OnPostLinkAsync(int id, string? code)
    {
        var normalized = Barcodes.Normalize(code);
        var m = await db.Medications.FirstOrDefaultAsync(x => x.Id == id && !x.Archived);
        if (m is null || normalized is null) return NotFound();
        m.Barcode = normalized;
        await db.SaveChangesAsync();
        log.LogInformation("Barcode linked to medication {MedicationId}", id);
        return RedirectToPage("/Medications/Details", new { id, done = "linked" });
    }
}
