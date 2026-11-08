using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Medications;

/// <summary>Pick a picture for a medication from Wikimedia Commons or DailyMed, searched by name.</summary>
public class ImageSearchModel(MedStockDb db, IImageSearch search, MedicationService service, IStringLocalizer<SharedResource> l)
    : PageModel
{
    public Medication Medication { get; private set; } = null!;
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int Offset { get; set; }
    [BindProperty(SupportsGet = true)] public ImageSource Source { get; set; } = ImageSource.Wikimedia;
    /// <summary>Only search after an explicit tap (the query is sent to Wikimedia), not when the page opens.</summary>
    [BindProperty(SupportsGet = true)] public bool Go { get; set; }
    public IReadOnlyList<ImageHit>? Hits { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var m = await db.Medications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Archived);
        if (m is null) return NotFound();
        Medication = m;
        Q = string.IsNullOrWhiteSpace(Q) ? m.Name : Q.Trim()[..Math.Min(100, Q.Trim().Length)];
        if (!Go) return Page();
        try { Hits = await search.SearchAsync(Source, Q, Offset, HttpContext.RequestAborted); }
        catch (ImageSearchUnavailableException) { Error = l["ImageSearch_Unavailable"]; }
        return Page();
    }

    public async Task<IActionResult> OnPostUseAsync(int id, string? url)
    {
        if (!ImageSearchService.IsAllowed(url)) return BadRequest();
        var bytes = await search.DownloadAsync(url!, HttpContext.RequestAborted);
        if (bytes is null) return await Failed(id, l["ImageSearch_DownloadFailed"]);
        try
        {
            using var ms = new MemoryStream(bytes);
            await service.SavePhotoAsync(id, ms);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidPhotoException) { return await Failed(id, l["Photo_Invalid"]); }
        return RedirectToPage("/Medications/Details", new { id });
    }

    private async Task<IActionResult> Failed(int id, string message)
    {
        var m = await db.Medications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();
        Medication = m;
        Q ??= m.Name;
        Error = message;
        Response.StatusCode = StatusCodes.Status502BadGateway;
        return Page();
    }
}
