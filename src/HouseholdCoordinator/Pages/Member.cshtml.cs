using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

// A member's own view: what has landed with this one person, and
// nothing that landed with anyone else.
//
// DEMO DELIVERY PATH ONLY. On this one computer, this page stands
// in for delivery so the demo's landings can run: Daniel's plan
// reaching Daniel, a settled dinner reaching both partners, a
// confirmed alert reaching the person it names, a re-timed entry
// reaching the person it affects. The product's design delivers
// through a person's own assistant, in their own channel; that
// cross-channel delivery is not built here, and this page does not
// pretend to be it.
public class MemberModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MemberDeliveryService _deliveries;
    private readonly ConfirmationService _confirmations;

    public MemberModel(AppDbContext db, MemberDeliveryService deliveries, ConfirmationService confirmations)
    {
        _db = db;
        _deliveries = deliveries;
        _confirmations = confirmations;
    }

    public List<Person> People { get; private set; } = new();
    public Person? Member { get; private set; }
    public List<MemberDeliveryItem> Received { get; private set; } = new();
    public string? Note => TempData["MemberNote"] as string;

    public async Task OnGetAsync(int? personId)
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var id = personId ?? People.FirstOrDefault()?.Id;
        Member = People.FirstOrDefault(p => p.Id == id);
        if (Member is not null)
        {
            Received = await _deliveries.ReceivedForAsync(Member.Id);
        }
    }

    // The addressee's closing tap, on the receiving surface itself:
    // the person a delivery landed with taps it done (for the trash
    // turn, the bin is out), and the tap goes through the one
    // confirmation service, which checks the tapper is the addressee
    // and writes the audit row. A tap offered on anyone else's view
    // is not rendered there at all, because this view only ever
    // holds the viewing person's own landings.
    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostCloseDeliveryAsync(int deliveryId, int personId)
    {
        try
        {
            await _confirmations.CloseDeliveryAsync(deliveryId, personId, "tap");
            TempData["MemberNote"] = "Closed. The record shows who closed it and when.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["MemberNote"] = ex.Message;
        }
        return RedirectToPage(new { personId });
    }
}
