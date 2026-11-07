using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MedStock.Web.Pages.Medications;

/// <summary>Archived medication (§3.11): restore, or delete permanently.</summary>
public class ArchivedModel(MedStockDb db, IClock clock, MedicationService service) : PageModel
{
    public List<IGrouping<Domain.Profile, MedicationRow>> Groups { get; private set; } = [];

    public async Task OnGetAsync() =>
        Groups = (await MedicationQueries.LoadAsync(db, clock.Today, archived: true))
            .OrderBy(r => r.Profile.SortOrder).ThenBy(r => r.Medication.Name, StringComparer.CurrentCultureIgnoreCase)
            .GroupBy(r => r.Profile, r => r, new ProfileById()).ToList();

    public async Task<IActionResult> OnPostRestoreAsync(int id)
    {
        try { await service.SetArchivedAsync(id, false); }
        catch (KeyNotFoundException) { }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try { await service.DeletePermanentlyAsync(id); }
        catch (KeyNotFoundException) { }
        return RedirectToPage();
    }

    private sealed class ProfileById : IEqualityComparer<Domain.Profile>
    {
        public bool Equals(Domain.Profile? x, Domain.Profile? y) => x?.Id == y?.Id;
        public int GetHashCode(Domain.Profile p) => p.Id;
    }
}
