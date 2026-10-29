using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// The connections view on the node: the door's state, in the open.
// Pending join requests render as inviter cards (claimed name,
// provider claim labeled self-asserted, the level and its plain
// consequences, the check phrase, the public key fingerprints),
// and the inviter confirms or declines from here; an invite alone
// grants nothing, and silence leaves a request pending. Confirmed
// connections render with their person, provider claim, level,
// fingerprints, enrolling inviter, and standing.
public class ConnectionsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DoorService _door;

    public ConnectionsModel(AppDbContext db, DoorService door)
    {
        _db = db;
        _door = door;
    }

    public List<Person> People { get; private set; } = new();
    public List<HouseConnection> Connections { get; private set; } = new();
    public List<JoinRequest> PendingRequests { get; private set; } = new();
    public Dictionary<int, DoorService.InviterCard> Cards { get; private set; } = new();
    public string? Note => TempData["Note"] as string;
    public string? NewInviteCode => TempData["NewInviteCode"] as string;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostInviteAsync(int inviterPersonId, int? intendedPersonId, string level)
    {
        var household = await _db.Households.FirstAsync();
        var code = await _door.CreateInviteAsync(household.Id, inviterPersonId, intendedPersonId, level);
        TempData["NewInviteCode"] = code;
        TempData["Note"] = "Invite created. It grants nothing by itself; share the code with the person, and confirm their join here when their assistant knocks.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int joinRequestId, int inviterPersonId, int personId, string checkPhrase)
    {
        try
        {
            var connection = await _door.ConfirmJoinAsync(joinRequestId, inviterPersonId, personId, checkPhrase);
            TempData["Note"] = $"Join confirmed. Connection {connection.ConnectionId} is active; its packets are now checked against that enrollment on every intake.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeclineAsync(int joinRequestId, int inviterPersonId)
    {
        try
        {
            await _door.DeclineJoinAsync(joinRequestId, inviterPersonId);
            TempData["Note"] = "Join declined and recorded. No connection was created, and the invite is spent.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string connectionId)
    {
        await _door.RevokeConnectionAsync(connectionId);
        TempData["Note"] = $"Connection {connectionId} revoked. It can submit nothing from now on; words already in the record stay.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        Connections = await _db.HouseConnections
            .Include(c => c.Person)
            .Include(c => c.EnrollingInviterPerson)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync();
        PendingRequests = await _db.JoinRequests
            .Where(r => r.Status == "Pending")
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync();
        foreach (var request in PendingRequests)
        {
            Cards[request.Id] = await _door.GetInviterCardAsync(request.Id);
        }
    }
}
