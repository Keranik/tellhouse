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

    // Set only when HouseSideOpen. The secret itself is never
    // carried in a URL, a redirect, or a hidden form field: the
    // unlock post exchanges it for the short-lived cookie below,
    // and every later request presents the cookie instead.
    public string? PresentedHouseSecret { get; private set; }

    public string? Note => TempData["Note"] as string;
    public string? NewInviteCode => TempData["NewInviteCode"] as string;
    public string? NewInviteFor => TempData["NewInviteFor"] as string;

    // The members' own assistants a visitor can ask to send the house a
    // note (the Test Send control), each with whether it is joined to
    // this household right now. Only a joined one gets a button.
    public List<(StandInEndpoint StandIn, bool Connected)> StandIns { get; private set; } = new();
    public string? TestSendNote => TempData["TestSendNote"] as string;

    // The household zone request times are shown in.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // The cookie the unlock post sets: HttpOnly, same-site, and
    // gone again in thirty minutes. It holds the presented secret
    // so the page's own posts stay authorized without the secret
    // ever riding in an address, a redirect, or a form field.
    private const string HouseSideCookie = "tellhouse-house-side";

    private string? PresentedSecret()
    {
        var fromHeader = Request.Headers["X-House-Secret"].ToString();
        if (!string.IsNullOrEmpty(fromHeader))
        {
            return fromHeader;
        }
        if (Request.Cookies.TryGetValue(HouseSideCookie, out var fromCookie)
            && !string.IsNullOrEmpty(fromCookie))
        {
            return fromCookie;
        }
        if (Request.HasFormContentType
            && Request.Form.TryGetValue("houseSecret", out var fromForm)
            && !string.IsNullOrEmpty(fromForm.ToString()))
        {
            return fromForm.ToString();
        }
        return null;
    }

    // A short display form of a key fingerprint: the first eight
    // characters, split in two groups of four.
    public string ShortKey(string fingerprint)
    {
        var chars = new string((fingerprint ?? "")
            .Where(char.IsLetterOrDigit)
            .Take(8)
            .ToArray()).ToLowerInvariant();
        return chars.Length > 4 ? $"{chars[..4]} {chars[4..]}" : chars;
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

    // The unlock: a POST, never a GET. A correct passcode is
    // exchanged for the short-lived cookie and the redirect that
    // follows carries nothing; a wrong one changes nothing.
    public IActionResult OnPostUnlock(string? houseSecret)
    {
        if (_house.Authorizes(houseSecret))
        {
            Response.Cookies.Append(HouseSideCookie, houseSecret!, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddMinutes(30),
                Path = "/"
            });
            TempData["Note"] = "Admin controls are unlocked for the next thirty minutes.";
        }
        else
        {
            TempData["Note"] = "That passcode does not match.";
        }
        return RedirectToPage();
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
        var intended = intendedPersonId is int intendedId
            ? await _db.Persons.FirstOrDefaultAsync(p => p.Id == intendedId)
            : null;
        TempData["NewInviteFor"] = NameWording.First(intended?.DisplayName);
        TempData["Note"] = "Invite created. It grants nothing by itself; share the code, and approve the join here when their assistant knocks.";
        return RedirectToPage();
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
            var person = await _db.Persons.FirstOrDefaultAsync(p => p.Id == connection.PersonId);
            TempData["Note"] = $"Approved. {NameWording.First(person?.DisplayName)}'s assistant is connected.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage();
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
            TempData["Note"] = "Declined. No connection was created.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Note"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string connectionId)
    {
        if (!AuthorizeHouse())
        {
            return StatusCode(403);
        }
        var connection = await _db.HouseConnections
            .Include(c => c.Person)
            .FirstOrDefaultAsync(c => c.ConnectionId == connectionId);
        await _door.RevokeConnectionAsync(connectionId);
        TempData["Note"] = $"{NameWording.First(connection?.Person?.DisplayName)}'s assistant can no longer send to this household. Past entries stay.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            HouseholdZone = HouseholdTime.ZoneFor(household.TimeZoneId);
        }
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
        if (HttpContext?.RequestServices?.GetService(typeof(TestSendService)) is TestSendService testSend)
        {
            foreach (var standIn in testSend.ConfiguredStandIns)
            {
                StandIns.Add((standIn, await testSend.IsConnectedAsync(standIn)));
            }
        }
    }
}
