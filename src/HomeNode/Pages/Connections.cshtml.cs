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
//
// This page performs the same door ceremony the HTTP /door/*
// adapter performs, so it carries the same house authorization:
// the ceremony handlers here refuse any caller who has not
// presented the house secret, through the
// same HouseAuthorization check the adapter uses, and a pending
// request's check phrase is rendered only to a viewer who has
// presented it. A browser cannot set the X-House-Secret header,
// so the page takes the secret in its own form field (or the
// header, when the caller can send one); the credential checked
// is the same one, and with no secret configured on the node the
// page's house side is closed, exactly as the adapter's is.
public class ConnectionsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DoorService _door;
    private readonly HouseAuthorization _house;

    public ConnectionsModel(AppDbContext db, DoorService door, HouseAuthorization house)
    {
        _db = db;
        _door = door;
        _house = house;
    }

    public List<Person> People { get; private set; } = new();
    public List<HouseConnection> Connections { get; private set; } = new();
    public List<JoinRequest> PendingRequests { get; private set; } = new();
    public Dictionary<int, DoorService.InviterCard> Cards { get; private set; } = new();

    // Whether the current viewer has presented the house secret.
    // The ceremony forms render only when this is true, the check
    // phrases render only when this is true, and every ceremony
    // handler re-checks it before acting.
    public bool HouseSideOpen { get; private set; }

    // The secret the viewer presented, carried into the page's
    // own forms so an authorized operator stays authorized across
    // the page's posts and redirects. Set only when HouseSideOpen.
    public string? PresentedHouseSecret { get; private set; }

    public string? Note => TempData["Note"] as string;
    public string? NewInviteCode => TempData["NewInviteCode"] as string;

    private string? PresentedSecret()
    {
        var fromHeader = Request.Headers["X-House-Secret"].ToString();
        if (!string.IsNullOrEmpty(fromHeader))
        {
            return fromHeader;
        }
        if (Request.HasFormContentType
            && Request.Form.TryGetValue("houseSecret", out var fromForm)
            && !string.IsNullOrEmpty(fromForm.ToString()))
        {
            return fromForm.ToString();
        }
        if (Request.Query.TryGetValue("houseSecret", out var fromQuery)
            && !string.IsNullOrEmpty(fromQuery.ToString()))
        {
            return fromQuery.ToString();
        }
        return null;
    }

    private bool AuthorizeHouse()
    {
        var presented = PresentedSecret();
        HouseSideOpen = _house.Authorizes(presented);
        PresentedHouseSecret = HouseSideOpen ? presented : null;
        return HouseSideOpen;
    }

    public async Task OnGetAsync()
    {
        AuthorizeHouse();
        await LoadAsync();
        if (!HouseSideOpen)
        {
            // The card is the inviter's evidence, and its check
            // phrase is the one part of it the ceremony's secrecy
            // rests on: it is never rendered to a viewer who has
            // not presented the house secret.
            foreach (var id in Cards.Keys.ToList())
            {
                Cards[id] = Cards[id] with { CheckPhrase = "" };
            }
        }
    }

    public async Task<IActionResult> OnPostInviteAsync(int inviterPersonId, int? intendedPersonId, string level)
    {
        if (!AuthorizeHouse())
        {
            return StatusCode(403);
        }
        var household = await _db.Households.FirstAsync();
        var code = await _door.CreateInviteAsync(household.Id, inviterPersonId, intendedPersonId, level);
        TempData["NewInviteCode"] = code;
        TempData["Note"] = "Invite created. It grants nothing by itself; share the code with the person, and confirm their join here when their assistant knocks.";
        return RedirectToPage(new { houseSecret = PresentedHouseSecret });
    }

    public async Task<IActionResult> OnPostConfirmAsync(int joinRequestId, int inviterPersonId, int personId, string checkPhrase)
    {
        if (!AuthorizeHouse())
        {
            return StatusCode(403);
        }
        try
        {
            var connection = await _door.ConfirmJoinAsync(joinRequestId, inviterPersonId, personId, checkPhrase);
            TempData["Note"] = $"Join confirmed. Connection {connection.ConnectionId} is active; everything it sends is now checked against this connection.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage(new { houseSecret = PresentedHouseSecret });
    }

    public async Task<IActionResult> OnPostDeclineAsync(int joinRequestId, int inviterPersonId)
    {
        if (!AuthorizeHouse())
        {
            return StatusCode(403);
        }
        try
        {
            await _door.DeclineJoinAsync(joinRequestId, inviterPersonId);
            TempData["Note"] = "Join declined and recorded. No connection was created, and the invite is spent.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage(new { houseSecret = PresentedHouseSecret });
    }

    public async Task<IActionResult> OnPostRevokeAsync(string connectionId)
    {
        if (!AuthorizeHouse())
        {
            return StatusCode(403);
        }
        await _door.RevokeConnectionAsync(connectionId);
        TempData["Note"] = $"Connection {connectionId} revoked. It can submit nothing from now on; words already in the record stay.";
        return RedirectToPage(new { houseSecret = PresentedHouseSecret });
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
