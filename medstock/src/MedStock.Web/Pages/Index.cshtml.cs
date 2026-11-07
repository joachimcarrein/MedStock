using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages;

public class IndexModel(MedStockDb db, IClock clock) : PageModel
{
    public bool HasProfiles { get; private set; }
    public List<MedicationRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        HasProfiles = await db.Profiles.AnyAsync();
        if (HasProfiles) Rows = await MedicationQueries.LoadAsync(db, clock.Today);
    }
}
