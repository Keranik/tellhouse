using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HomeNode.Pages;

// The status-code landing for addresses the house does not have.
// Reached through UseStatusCodePagesWithReExecute, which keeps the
// original status code on the response.
public class NotFoundModel : PageModel
{
    public void OnGet()
    {
    }
}
