using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MedStock.Web.Pages.Medications;

public class DetailsModel(MedStockDb db, IClock clock, AppPaths paths) : PageModel
{
    public MedicationRow Row { get; private set; } = null!;
    public long PhotoVersion { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var row = (await MedicationQueries.LoadAsync(db, clock.Today, id: id)).SingleOrDefault();
        if (row is null) return NotFound();
        Row = row;
        PhotoVersion = PhotoModel.Version(paths, id);
        return Page();
    }
}
