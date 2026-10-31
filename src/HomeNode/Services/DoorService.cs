using System.Security.Cryptography;
using HomeNode.Data;
using HomeNode.Protocol;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// The door: how a member's assistant joins this house, and the
// valve a person holds over what their assistant may send.
//
// Join flow, in order, with no step skippable:
//   1. A person in the house creates an invite. The invite grants
//      nothing by itself; only its hash is stored.
//   2. The joiner's assistant knocks with the invite code, a
//      claimed name, a provider claim, and its own freshly
//      generated public keys. The knock spends the invite, win or
//      lose, and creates a pending join request carrying a check
//      phrase: a few plain words the joiner sees on their side and
//      the inviter sees on the inviter card.
//   3. The inviter reads the card: who says they are joining,
//      through which assistant (labeled self-asserted), what the
//      level lets them do and not do, the public key fingerprints,
//      and the check phrase, compared over a channel the family
//      already trusts.
//   4. The inviter explicitly confirms (check phrase must match)
//      or explicitly declines. Silence is never a yes: a pending
//      request does nothing until one of those two acts. Declining
//      is a recorded disposal, not an absence.
//   5. Only a confirmation creates a connection, with the
//      fingerprints in the registry and the standing its level
//      names (Active for Standard, KnockOnly for a KnockOnly
//      invite). Permissions are then enforced on every packet
//      intake and every delivery, not only at the door.
//
// Consent records are the valve: created by read back, expiring,
// revocable in one act, forward looking only.
public class DoorService
{
    private readonly AppDbContext _db;

    public DoorService(AppDbContext db)
    {
        _db = db;
    }

    private static readonly string[] PhraseWords =
    {
        "apple", "bridge", "candle", "meadow", "harbor", "lantern",
        "willow", "kettle", "garden", "button", "saddle", "window",
        "basket", "pepper", "orchard", "blanket", "feather", "market",
        "pocket", "river", "stone", "table", "village", "winter"
    };

    public static string NewCheckPhrase()
    {
        var picks = new string[4];
        for (var i = 0; i < picks.Length; i++)
        {
            picks[i] = PhraseWords[RandomNumberGenerator.GetInt32(PhraseWords.Length)];
        }
        return string.Join(" ", picks);
    }

    public static string HashInviteCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

    // Creates an invite and returns the plain code once. Default
    // life is a day at most, per the door design.
    public async Task<string> CreateInviteAsync(
        int householdId, int inviterPersonId, int? intendedPersonId, string level, TimeSpan? lifetime = null)
    {
        var code = $"invite-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))}";
        _db.DoorInvites.Add(new DoorInvite
        {
            CodeHash = HashInviteCode(code),
            HouseholdId = householdId,
            InviterPersonId = inviterPersonId,
            IntendedPersonId = intendedPersonId,
            Level = level,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(24))
        });
        await _db.SaveChangesAsync();
        return code;
    }

    // The knock. Returns the join request, including the check
    // phrase the joiner's side shows the joiner. Throws when the
    // invite is unknown, expired, or already spent: a lapsed or
    // used invite never fails silently into a weaker check.
    // The door fingerprint: lowercase hex SHA-256 of the 65 byte
    // uncompressed public point (protocol contract section 1).
    public static string FingerprintOf(string publicKeyBase64Url)
    {
        var point = Base64Url.Decode(publicKeyBase64Url);
        IdentityKeypair.SplitPoint(point);
        return Convert.ToHexStringLower(SHA256.HashData(point));
    }

    public async Task<JoinRequest> KnockAsync(
        string inviteCode, string claimedName, string claimedPartyId, string providerClaim,
        string publicKeyBase64Url)
    {
        var invite = await _db.DoorInvites.FirstOrDefaultAsync(i => i.CodeHash == HashInviteCode(inviteCode))
            ?? throw new InvalidOperationException("That invite is not one this house issued.");
        return await KnockOnInviteAsync(invite, claimedName, claimedPartyId, providerClaim, publicKeyBase64Url);
    }

    // The knock against an invite row the caller has already
    // identified. The HTTP door adapter identifies
    // the invite by its intended person, because the frozen
    // stand-in knock body carries no invite code field; the code
    // itself is proven by the credential the joiner derives from
    // it, checked at /door/status. The ceremony is unchanged: the
    // invite is spent here, win or lose, and the check phrase is
    // generated here and shown only on the inviter card and to
    // the joiner over the trusted channel.
    public async Task<JoinRequest> KnockOnInviteAsync(
        DoorInvite invite, string claimedName, string claimedPartyId, string providerClaim,
        string publicKeyBase64Url)
    {
        if (invite.UsedAtUtc is not null)
        {
            throw new InvalidOperationException("That invite has already been used; it is spent.");
        }
        if (invite.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("That invite has lapsed. A fresh invite is needed.");
        }
        invite.UsedAtUtc = DateTime.UtcNow;
        var request = new JoinRequest
        {
            DoorInviteId = invite.Id,
            ClaimedName = claimedName,
            ClaimedPartyId = claimedPartyId,
            // The connection id is generated exactly once, here
            // and every later step (card, confirm,
            // status, the connection itself) reads it from this
            // row instead of deriving it again.
            ConnectionId = ConnectionIdForParty(claimedPartyId),
            ProviderClaim = providerClaim,
            PublicKeyBase64Url = publicKeyBase64Url,
            Fingerprint = FingerprintOf(publicKeyBase64Url),
            CheckPhrase = NewCheckPhrase(),
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.JoinRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    // The inviter card: everything the inviter decides on, in
    // family language, with the provider claim labeled as what it
    // is. The card is data here; the Connections page renders it.
    public record InviterCard(
        int JoinRequestId,
        string ClaimedName,
        string ClaimedPartyId,
        string ProviderClaim,
        string ProviderClaimLabel,
        string Level,
        string LevelConsequences,
        string CheckPhrase,
        string Fingerprint,
        string InviterName,
        DateTime CreatedAtUtc);

    public async Task<InviterCard> GetInviterCardAsync(int joinRequestId)
    {
        var request = await _db.JoinRequests
            .Include(r => r.DoorInvite).ThenInclude(i => i!.InviterPerson)
            .FirstOrDefaultAsync(r => r.Id == joinRequestId)
            ?? throw new InvalidOperationException($"Join request {joinRequestId} does not exist.");
        var level = request.DoorInvite!.Level;
        return new InviterCard(
            request.Id,
            request.ClaimedName,
            request.ClaimedPartyId,
            request.ProviderClaim,
            "their own claim, not checked",
            level,
            LevelConsequencesFor(level),
            request.CheckPhrase,
            request.Fingerprint,
            request.DoorInvite.InviterPerson?.DisplayName ?? "the inviter",
            request.CreatedAtUtc);
    }

    public static string LevelConsequencesFor(string level) => level switch
    {
        "Standard" => "Can leave short notes and ask questions at this level, and receive what is addressed to them. Cannot see health related entries, invite others, or change anyone's access.",
        "KnockOnly" => "Can send a join or re-confirmation request and nothing else. No notes, no questions, nothing delivered.",
        _ => $"Level {level}, as the inviter named it. The house enforces this person's permissions on every request at this level."
    };

    // The inviter confirms. The check phrase the inviter heard from
    // the joiner must match the one on the request, and the person
    // confirming must be the invite's named inviter. Only then
    // does a connection exist.
    public async Task<HouseConnection> ConfirmJoinAsync(
        int joinRequestId, int inviterPersonId, int personId, string checkPhraseHeard)
    {
        var request = await LoadPendingAsync(joinRequestId);
        if (request.DoorInvite!.InviterPersonId != inviterPersonId)
        {
            throw new InvalidOperationException("Only the named inviter can confirm this join.");
        }
        if (!string.Equals(request.CheckPhrase, checkPhraseHeard.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The check phrase does not match. This is not that person's request.");
        }
        // Re-enrollment replaces, never duplicates: a
        // member who joins again with a new key takes over the one
        // connection row their connection id names. Two rows under
        // one id left every reader free to pick a different one:
        // intake verified signatures against the old key while
        // deliveries sealed to the new one, and revoking the id
        // revoked only whichever row a lookup happened to find.
        var connectionId = request.ConnectionId ?? ConnectionIdForParty(request.ClaimedPartyId);
        var connection = await _db.HouseConnections
            .FirstOrDefaultAsync(c => c.ConnectionId == connectionId);
        if (connection is null)
        {
            connection = new HouseConnection { ConnectionId = connectionId };
            _db.HouseConnections.Add(connection);
        }
        connection.HouseholdId = request.DoorInvite.HouseholdId;
        connection.PersonId = personId;
        connection.PartyId = request.ClaimedPartyId;
        connection.ProviderClaim = request.ProviderClaim;
        connection.Level = request.DoorInvite.Level;
        // The level is the standing: a KnockOnly
        // invite confirms to a KnockOnly connection, which may
        // present only a knock, and never silently becomes a
        // fully Active full-access connection.
        connection.Standing = ConnectionAccess.StandingForLevel(request.DoorInvite.Level);
        connection.PublicKeyBase64Url = request.PublicKeyBase64Url;
        connection.Fingerprint = request.Fingerprint;
        connection.EnrollingInviterPersonId = inviterPersonId;
        connection.CreatedAtUtc = DateTime.UtcNow;
        connection.LastSessionAtUtc = null;
        connection.RevokedAtUtc = null;
        // Any other live connection this person holds (an earlier
        // enrollment under a different id) is superseded by this
        // one and revoked in the same act, so the person has
        // exactly one live connection: the one just confirmed.
        var superseded = await _db.HouseConnections
            .Where(c => c.PersonId == personId && c.ConnectionId != connectionId
                        && c.Standing != ConnectionStanding.Revoked)
            .ToListAsync();
        foreach (var old in superseded)
        {
            old.Standing = ConnectionStanding.Revoked;
            old.RevokedAtUtc = DateTime.UtcNow;
        }
        request.Status = "Confirmed";
        request.DecidedByPersonId = inviterPersonId;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.ResultConnectionId = connection.ConnectionId;
        await _db.SaveChangesAsync();
        return connection;
    }

    // Connection ids for the frozen scope parties are the
    // contract's scope ids, so the relay registry, the stand-ins,
    // and this registry all name one connection. Any other party
    // gets a generated id.
    public static string ConnectionIdForParty(string partyId) => partyId switch
    {
        "member-lisa" => "conn-lisa-01",
        "member-daniel" => "conn-daniel-01",
        _ => $"conn-{Guid.NewGuid():N}"[..21]
    };

    // The explicit decline: a recorded disposal. No connection is
    // created, the invite stays spent, and any packet offered on
    // this join afterward meets no connection and is rejected.
    public async Task DeclineJoinAsync(int joinRequestId, int inviterPersonId)
    {
        var request = await LoadPendingAsync(joinRequestId);
        if (request.DoorInvite!.InviterPersonId != inviterPersonId)
        {
            throw new InvalidOperationException("Only the named inviter can decline this join.");
        }
        request.Status = "Declined";
        request.DecidedByPersonId = inviterPersonId;
        request.DecidedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // Revocation is one act, effective immediately: the connection
    // stops accepting packets and sessions from this moment. Words
    // already in the record stay, as a departed member's words stay.
    // The act covers every row the id names: duplicates
    // written before re-enrollment replaced in place must not leave
    // a live row behind under a revoked id.
    public async Task RevokeConnectionAsync(string connectionId)
    {
        var rows = await _db.HouseConnections
            .Where(c => c.ConnectionId == connectionId)
            .ToListAsync();
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Connection {connectionId} does not exist.");
        }
        foreach (var row in rows)
        {
            row.Standing = ConnectionStanding.Revoked;
            row.RevokedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    // Exact person lookup by name, for the door's HTTP adapter
    // A name matches a person only when it is their
    // full display name or their exact first name; a prefix, a
    // single letter, or an empty string matches nobody. Prefix
    // matching resolved "" and "M" to the first Margaret in the
    // list, silently enrolling joins under the wrong inviter.
    public static Person? FindPersonByName(IEnumerable<Person> people, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var wanted = name.Trim();
        return people.FirstOrDefault(p =>
            string.Equals(p.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.DisplayName.Split(' ')[0], wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SetKnockOnlyAsync(string connectionId)
    {
        var connection = await _db.HouseConnections.FirstOrDefaultAsync(c => c.ConnectionId == connectionId)
            ?? throw new InvalidOperationException($"Connection {connectionId} does not exist.");
        connection.Standing = ConnectionStanding.KnockOnly;
        await _db.SaveChangesAsync();
    }

    // The valve. CreatedByReadBack is required: a Consent the
    // person has not heard read back and confirmed is not a valve
    // opening, it is a form. Expiry is required; standing consent
    // is never perpetual.
    //
    // How the valve meets the frozen protocol contract: the plain
    // packet carries no valve basis field (its body is exactly
    // kind and text), so the valve is enforced on the node side,
    // from Consent state, mapped by kind: a raw_telling needs a
    // OneLiner Consent, a question needs a Question Consent, an
    // acknowledgement disposing a proposal needs a
    // ProposalResponse Consent. No Consent in force for the kind
    // means the valve is closed and the packet is rejected whole.
    // This mapping is the home node's, flagged in the open as a
    // proposal, because the stand-ins' draft in-band consent object did
    // not survive the protocol freeze.
    public async Task<ConsentRecord> CreateConsentAsync(
        int householdId, int personId, string materialClass, string contentClass,
        TimeSpan lifetime, bool createdByReadBack)
    {
        if (!createdByReadBack)
        {
            throw new InvalidOperationException("A Consent is created by read back, or it is not created.");
        }
        if (materialClass is not ("OneLiner" or "Question" or "ProposalResponse"))
        {
            throw new InvalidOperationException($"Material class {materialClass} is outside the closed list.");
        }
        if (contentClass is not ("ScheduleFacts" or "SupplyFacts" or "OwnWhereabouts" or "FreeTextOneLiners"))
        {
            throw new InvalidOperationException($"Content class {contentClass} is outside the closed list.");
        }
        var consent = new ConsentRecord
        {
            ConsentId = $"consent-{Guid.NewGuid():N}"[..24],
            HouseholdId = householdId,
            PersonId = personId,
            MaterialClass = materialClass,
            ContentClass = contentClass,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
            CreatedByReadBack = true
        };
        _db.ConsentRecords.Add(consent);
        await _db.SaveChangesAsync();
        return consent;
    }

    public async Task RevokeConsentAsync(string consentId)
    {
        var consent = await _db.ConsentRecords.FirstOrDefaultAsync(c => c.ConsentId == consentId)
            ?? throw new InvalidOperationException($"Consent {consentId} does not exist.");
        consent.RevokedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<JoinRequest> LoadPendingAsync(int joinRequestId)
    {
        var request = await _db.JoinRequests
            .Include(r => r.DoorInvite)
            .FirstOrDefaultAsync(r => r.Id == joinRequestId)
            ?? throw new InvalidOperationException($"Join request {joinRequestId} does not exist.");
        if (request.Status != "Pending")
        {
            throw new InvalidOperationException($"Join request {joinRequestId} is already {request.Status}.");
        }
        return request;
    }
}
