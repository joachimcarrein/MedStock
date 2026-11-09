using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MedStock.Web.Pages.Settings;

/// <summary>Import of a JSON export (adds, never overwrites). GET shows the form, POST imports and shows the result.</summary>
public class ImportModel(ImportService import, CurrentUser user) : PageModel
{
    public ImportResult? Result { get; private set; }

    public void OnGet() { }

    [RequestSizeLimit(ImportService.MaxBytes + 100_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImportService.MaxBytes + 100_000)]
    public async Task<IActionResult> OnPostAsync(IFormFile? file, bool applySettings)
    {
        if (file is null || file.Length == 0 || file.Length > ImportService.MaxBytes)
        {
            Result = new ImportResult(0, 0, 0, 0, false, "Import_NoFile");
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        await using var stream = file.OpenReadStream();
        Result = await import.ImportAsync(stream, applySettings, user.Id, HttpContext.RequestAborted);
        if (Result.Error is not null) Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
