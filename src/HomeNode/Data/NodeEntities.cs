namespace HomeNode.Data;

// The home node's federation tables. Everything in this file lives
// only on the node: the connection registry, the door's invites and
// join requests, Consent records (the valve), the sealed packet
// inbox, the packet trail, session challenges, and the sealed
// delivery outbox. No plaintext record content is stored in any of
// these tables: the inbox and the outbox hold ciphertext only, and
// the registry holds public keys and their fingerprints, never a
// private key. The record itself stays in the tables in Entities.cs,
// exactly as the house layer kept it.

// Where a connection stands with the house. An invite grants
// nothing by itself; a connection exists only after the named
// inviter confirms the join (see DoorService), and only an Active
// connection may submit packets or open a session.
public enum ConnectionStanding
{
    Active,
    KnockOnly,
    Revoked
}

public class HouseConnection
{
    public int Id { get; set; }

    // The public connection id packets carry in their envelope.
    public string ConnectionId { get; set; } = "";
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    // The one person this connection speaks for, resolved from the
    // enrollment, never from anything a packet claims.
    public int PersonId { get; set; }
    public Person? Person { get; set; }

    // The assistant provider name the joiner asserted. Self-asserted
    // on every surface; the house verifies no provider claim.
    public string ProviderClaim { get; set; } = "";

    // The level of access the inviter confirmed, in plain words.
    public string Level { get; set; } = "";

    public ConnectionStanding Standing { get; set; } = ConnectionStanding.KnockOnly;

    // The enrolled party id the plain packet's sender_id and
    // recipient_id are checked against (for example member-lisa).
    // Resolved at enrollment, never from packet content.
    public string PartyId { get; set; } = "";

    // One identity keypair per party (protocol contract section 1):
    // the same P-256 keypair serves ECDH sealing and ECDSA
    // signing. Only the public half is stored here, as the
    // base64url uncompressed point; the member's private half is
    // generated on the member's own side and never crosses a wire.
    // Fingerprint is the door fingerprint: lowercase hex SHA-256
    // of the 65 byte uncompressed public point.
    public string PublicKeyBase64Url { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    // The named inviter who confirmed this join.
    public int EnrollingInviterPersonId { get; set; }
    public Person? EnrollingInviterPerson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSessionAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

// A knock, not a key: a long random one time code plus the house
// address. It contains no personal information and grants nothing.
// Only its hash is stored. The moment a join request is submitted
// with it, the invite is spent, whether the request is approved or
// denied.
public class DoorInvite
{
    public int Id { get; set; }
    public string CodeHash { get; set; } = "";
    public int HouseholdId { get; set; }
    public int InviterPersonId { get; set; }
    public Person? InviterPerson { get; set; }

    // The person the inviter means to invite, when they name one.
    public int? IntendedPersonId { get; set; }
    public Person? IntendedPerson { get; set; }

    public string Level { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
}

// One join request made against one invite. The inviter's card is
// rendered from this row: claimed name, provider claim labeled
// self-asserted, the level's plain consequences, and the check
// phrase, a few plain words the joiner's assistant shows the
// joiner and that also appear on the card, compared over a channel
// the family already trusts. Doing nothing means no: a request
// stays Pending until the inviter explicitly confirms or declines
// it, and both decisions are recorded here.
public class JoinRequest
{
    public int Id { get; set; }
    public int DoorInviteId { get; set; }
    public DoorInvite? DoorInvite { get; set; }

    public string ClaimedName { get; set; } = "";
    public string ProviderClaim { get; set; } = "";
    // The party id the joiner claims for their enrollment.
    public string ClaimedPartyId { get; set; } = "";
    // The connection id this request will enroll under, generated
    // once at knock time. For the frozen scope parties
    // it is the contract's scope id; for anyone else it is a
    // generated id, and generating it again at card or confirm
    // time would name a different connection every call, which is
    // how a knock for a non-scope party used to return an id the
    // card and confirm endpoints then reported as unknown. Null
    // only on requests written before the id was stored.
    public string? ConnectionId { get; set; }
    public string PublicKeyBase64Url { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string CheckPhrase { get; set; } = "";

    // Pending, Confirmed, or Declined.
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; }
    public int? DecidedByPersonId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? ResultConnectionId { get; set; }
}

// A Consent record: the valve as data. A person opens a valve by
// creating one Consent for one narrow material class, narrowed
// further by content class, with a required expiry, created by
// read back. A packet that names a Consent outside its class, or
// expired, or revoked, is rejected whole. Revocation is one act,
// effective immediately, and forward looking only.
public class ConsentRecord
{
    public int Id { get; set; }
    public string ConsentId { get; set; } = "";
    public int HouseholdId { get; set; }
    public int PersonId { get; set; }
    public Person? Person { get; set; }

    // MaterialClass: OneLiner, Question, or ProposalResponse.
    public string MaterialClass { get; set; } = "";
    // ContentClass, the closed narrow list: ScheduleFacts,
    // SupplyFacts, OwnWhereabouts, FreeTextOneLiners.
    public string ContentClass { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool CreatedByReadBack { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

// One packet as it arrived at the node, before opening. The
// envelope fields are exactly the relay visible list and nothing
// else; the body stays ciphertext until ProcessPacketAsync opens
// it inside the house boundary. A queued packet is unrecorded: its
// telling exists in no record table until the node takes it in,
// stamps RecordedAtUtc, and only then does the record hold it.
public class PacketInbox
{
    public int Id { get; set; }
    public string PacketId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    // The full canonical wire packet (ciphertext, envelope,
    // signature), exactly as it arrived.
    public string WireJson { get; set; } = "";
    // The envelope in canonical form, for views that show only
    // the relay visible fields.
    public string EnvelopeJson { get; set; } = "";
    public string CiphertextBase64 { get; set; } = "";
    public string SignatureBase64 { get; set; } = "";

    // QueuedUnrecorded, Recorded, or Rejected.
    public string Status { get; set; } = "QueuedUnrecorded";
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? RecordedAtUtc { get; set; }

    // The time the teller told it, learned only by opening the
    // packet. Null while queued: an unopened packet has no told
    // time the node can state.
    public DateTime? ToldAtUtc { get; set; }
    public string? RejectionReason { get; set; }

    // The record events this packet produced, as a JSON array of
    // event ids, written when the packet is recorded.
    public string? ResultEventIds { get; set; }
}

// One line per packet outcome, accepted or rejected: the visible
// trail the regression net asks for. A rejected packet leaves no
// event, only a line here. Acknowledgement lines record the act as
// reported, not observed.
public class PacketTrailLine
{
    public int Id { get; set; }
    public string PacketId { get; set; } = "";
    public string? ConnectionId { get; set; }
    // In or Out.
    public string Direction { get; set; } = "In";
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
    public int? EventId { get; set; }
    public DateTime AtUtc { get; set; }
}

// A session open exchange (protocol contract section 7). The
// member's knock packet carries a client nonce; the node's
// challenge packet carries a server nonce; the member's response
// proof signs the transcript under the member key. The node keeps
// at most one outstanding challenge per connection; a challenge
// lives 120 seconds and is single use. Once Verified, the session
// is open for that connection.
public class SessionChallenge
{
    public int Id { get; set; }
    public string ConnectionId { get; set; } = "";
    public string ClientNonce { get; set; } = "";
    public string ServerNonce { get; set; } = "";
    // The packet id of the node's challenge delivery packet; the
    // response acknowledgement names it in acked_packet_id.
    public string ChallengePacketId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool Verified { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public bool Consumed { get; set; }
}

// The seen registry, persisted on the node (protocol contract
// section 8, step 3): every packet_id whose envelope parsed is
// recorded here at the envelope step, before opening, whether
// the packet is later admitted or rejected. A second arrival of
// the same packet_id is a replay and is rejected at that step.
public class PacketSeen
{
    public int Id { get; set; }
    public string PacketId { get; set; } = "";
    public string? ConnectionId { get; set; }
    public DateTime SeenAtUtc { get; set; }
}

// Packet nonce single use, per connection (protocol contract
// section 8, step 7): recorded after a packet opens and its plain
// form validates. A fresh packet_id carrying a nonce already used
// on that connection is still a replay.
public class PacketNonceUse
{
    public int Id { get; set; }
    public string ConnectionId { get; set; } = "";
    public string PacketNonce { get; set; } = "";
    public string PacketId { get; set; } = "";
    public DateTime UsedAtUtc { get; set; }
}

// One sealed delivery waiting for one remote member. This replaces
// direct in-database delivery for members who hold a connection:
// no plaintext body exists in this table, only a packet sealed to
// that member's encryption key, signed by the node. Delivery is
// complete in the record when the member's acknowledgement packet
// comes back, and that acknowledgement is recorded as reported,
// not observed.
public class SealedOutboxItem
{
    public int Id { get; set; }
    public string PacketId { get; set; } = "";
    public int ToPersonId { get; set; }
    public Person? ToPerson { get; set; }
    public string ConnectionId { get; set; } = "";
    // The full canonical wire packet, as posted to the relay.
    public string WireJson { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string CiphertextBase64 { get; set; } = "";
    public string SignatureBase64 { get; set; } = "";

    // Alert, Plan, Answer, or Delivery.
    public string SourceKind { get; set; } = "Delivery";

    // A one-way dedupe fingerprint for landings that must not
    // repeat (the same plan words to the same person): the hash of
    // the landing's identity and words, never the words.
    public string? DedupeKey { get; set; }
    public int? SourceEventId { get; set; }
    public int? SourcePlanElementId { get; set; }

    // Queued, Sent, or Acknowledged.
    public string Status { get; set; } = "Queued";
    public DateTime CreatedAtUtc { get; set; }
    // When the relay loop last posted this packet to the relay
    // The relay holds a store-and-forward packet for
    // 300 seconds; a Sent item with no acknowledgement whose last
    // post is older than that is owed again, and the loop queues
    // it for another post instead of leaving it Sent forever.
    public DateTime? SentAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AckPacketId { get; set; }
}
