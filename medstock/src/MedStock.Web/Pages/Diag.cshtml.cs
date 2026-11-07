using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MedStock.Web.Pages;

/// <summary>Checkpoint A diagnostics. Remove in step 9.</summary>
public class DiagModel(IHaClient ha, CurrentUser currentUser) : PageModel
{
    public new CurrentUser User => currentUser;
    public IReadOnlyList<string> Services { get; private set; } = [];
    public string? ServicesError { get; private set; }
    public string? Slug { get; private set; }
    public bool Redirected { get; private set; }
    [TempData] public string? NotifyResult { get; set; }

    public string Header(string name) => Request.Headers[name].FirstOrDefault() is { Length: > 0 } v ? v : "(missing)";

    public async Task OnGetAsync(bool redirected = false)
    {
        Redirected = redirected;
        await LoadAsync();
    }

    public IActionResult OnGetPartial() =>
        Content($"<span class=\"ok\">HTMX works ({DateTime.Now:HH:mm:ss})</span>", "text/html");

    public IActionResult OnPostRedirect() => RedirectToPage("/Diag", new { redirected = true });

    public async Task<IActionResult> OnPostNotifyAsync(string[] services, string variant)
    {
        Slug = await ha.GetSelfSlugAsync(HttpContext.RequestAborted);
        var link = Slug is null ? null : variant == "panel" ? "/" + Slug : "/app/" + Slug;
        var results = new List<string>();
        foreach (var s in services)
        {
            var r = await ha.NotifyAsync(s, "MedStock", $"Test notification ({variant}): tap to open MedStock", link, "medstock-diag", HttpContext.RequestAborted);
            results.Add($"{s}: {r}");
        }
        NotifyResult = services.Length == 0 ? "No device selected." : $"Deep link {link ?? "(none)"} — " + string.Join(", ", results);
        return RedirectToPage("/Diag");
    }

    private async Task LoadAsync()
    {
        try { Services = await ha.GetNotifyServicesAsync(HttpContext.RequestAborted); }
        catch (HaUnavailableException ex) { ServicesError = "Home Assistant not reachable: " + ex.Message; }
        Slug = await ha.GetSelfSlugAsync(HttpContext.RequestAborted);
    }
}
