using HomeNode.Data;

namespace HomeNode.Services;

// Who "you" are on this demo, across every page: one choice, made
// once with the person switcher, kept in a cookie. Every page that
// shows the house through someone's eyes uses it as its default; a
// page reached with its own person in the link (?viewerId=, ?actorId=,
// ?personId=) still shows that person, so every existing link keeps
// meaning what it meant.
//
// It is a demo convenience, said plainly on the switcher: anyone may
// look through anyone's eyes here because everyone in it is invented.
// In the product a person reaches only their own view, through the
// door. The cookie names a person id and nothing else; it grants
// nothing, and every page still applies the same audience fence to
// whoever it shows.
//
// Pages built outside a request (the suites construct page models
// directly) have no HttpContext, so this returns null there and every
// page keeps its own default exactly as before.
public static class CurrentPerson
{
    public const string CookieName = "th-person";

    // The query keys the pages use to name a person in a link. The
    // switcher drops them from the address it returns to, so the
    // choice just made is the one the page shows.
    public static readonly string[] PersonKeys = { "viewerId", "actorId", "personId", "speakerId" };

    public static int? FromCookie(HttpContext? http)
    {
        if (http?.Request.Cookies.TryGetValue(CookieName, out var raw) == true
            && int.TryParse(raw, out var id) && id > 0)
        {
            return id;
        }
        return null;
    }

    // The person a page shows: its own id when the link named one,
    // otherwise the switcher's choice, checked against the household.
    public static int? Resolve(HttpContext? http, int? explicitId, IEnumerable<Person> people)
    {
        if (explicitId is not null)
        {
            return explicitId;
        }
        var chosen = FromCookie(http);
        return chosen is int id && people.Any(p => p.Id == id) ? id : null;
    }

    public static void Set(HttpContext http, int? personId)
    {
        if (personId is int id && id > 0)
        {
            http.Response.Cookies.Append(CookieName, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = http.Request.IsHttps,
                    IsEssential = true,
                    MaxAge = TimeSpan.FromDays(30),
                    Path = "/"
                });
        }
        else
        {
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        }
    }

    // A safe place to go back to: a local path only, never another
    // site, with the person keys removed.
    public static string SafeReturn(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith('/') || target.StartsWith("//") || target.StartsWith("/\\"))
        {
            return "/";
        }
        var q = target.IndexOf('?');
        if (q < 0)
        {
            return target;
        }
        var path = target[..q];
        var hash = "";
        var query = target[(q + 1)..];
        var h = query.IndexOf('#');
        if (h >= 0)
        {
            hash = query[h..];
            query = query[..h];
        }
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !PersonKeys.Any(k => pair.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return path + (kept.Count > 0 ? "?" + string.Join("&", kept) : "") + hash;
    }

    // The switcher link for a person (or the household when null),
    // returning to the given address.
    public static string SwitchHref(int? personId, string returnTo) =>
        (personId is int id ? $"/as/{id}" : "/as") + "?return=" + Uri.EscapeDataString(SafeReturn(returnTo));
}
