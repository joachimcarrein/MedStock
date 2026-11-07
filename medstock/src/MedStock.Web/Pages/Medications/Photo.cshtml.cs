using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace MedStock.Web.Pages.Medications;

/// <summary>Serves and stores the photo of a medication (§3.9). No view: every handler returns a result.</summary>
// Filter attributes only work on the page model class in Razor Pages, not on handler methods.
[RequestSizeLimit(MedicationService.MaxPhotoBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MedicationService.MaxPhotoBytes)]
public class PhotoModel(MedicationService service, AppPaths paths, IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>Cache-busting version: last write ticks of the photo file, 0 when there is none.</summary>
    public static long Version(AppPaths paths, int id)
    {
        var file = new FileInfo(paths.PhotoPath(id));
        return file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
    }

    public IActionResult OnGet(int id)
    {
        var file = new FileInfo(paths.PhotoPath(id));
        if (!file.Exists) return NotFound();
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        var etag = new EntityTagHeaderValue($"\"{file.LastWriteTimeUtc.Ticks:x}\"");
        // PhysicalFile handles If-None-Match / If-Modified-Since (304) and range requests.
        return new PhysicalFileResult(file.FullName, ContentType(file.FullName)) { LastModified = file.LastWriteTimeUtc, EntityTag = etag };
    }

    public async Task<IActionResult> OnPostAsync(int id, IFormFile? photo)
    {
        if (photo is null || photo.Length == 0 || photo.Length > MedicationService.MaxPhotoBytes) return Invalid();
        try
        {
            await using var stream = photo.OpenReadStream();
            await service.SavePhotoAsync(id, stream);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidPhotoException) { return Invalid(); }
        return IsFetch() ? new NoContentResult() : RedirectToPage("/Medications/Details", new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try { await service.DeletePhotoAsync(id); }
        catch (KeyNotFoundException) { return NotFound(); }
        return RedirectToPage("/Medications/Details", new { id });
    }

    private bool IsFetch() => Request.Headers.ContainsKey("RequestVerificationToken");

    private IActionResult Invalid() => new ContentResult
    {
        StatusCode = StatusCodes.Status400BadRequest, Content = l["Photo_Invalid"], ContentType = "text/plain; charset=utf-8",
    };

    /// <summary>The file is always named .jpg; a PNG upload keeps its PNG bytes, so sniff the real type.</summary>
    private static string ContentType(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var f = System.IO.File.OpenRead(path);
        return f.Read(head) == 4 && head[0] == 0x89 && head[1] == 0x50 ? "image/png" : "image/jpeg";
    }
}
