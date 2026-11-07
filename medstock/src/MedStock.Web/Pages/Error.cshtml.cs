using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MedStock.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    public int? Code { get; private set; }
    public void OnGet(int? code) => Code = code;
    public void OnPost(int? code) => Code = code;
}
