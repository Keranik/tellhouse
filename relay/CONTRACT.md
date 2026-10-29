# Relay contract

Status: this document describes the relay as built, against the
frozen protocol contract in src/Tellhouse.Protocol/CONTRACT.md.
Where the protocol's frozen test vectors differ from this
document, the vectors govern. Divergences are flagged below as
proposals, never silently resolved.

All four packets from the protocol's vectors file (telling,
delivery, acknowledgement, delivery) cross this relay unchanged,
in both directions, in the check suite (fixture snapshot in
tests/fixtures/lane_k_vectors.json).

## Wire shape (from the protocol contract)

A packet is one JSON object with exactly three keys:

| Key | Meaning |
| --- | --- |
| ciphertext | Base64url (no padding) sealed blob, 1 to 65,536 bytes |
| envelope | Object with exactly the eight fields below |
| signature | Base64url (no padding), decodes to exactly 64 bytes |

The envelope fields, exactly the relay-visible list:

| Field | Meaning |
| --- | --- |
| packet_id | Unique packet identifier, 1 to 128 characters |
| household_endpoint | Routing address of the household node |
| connection_id | Enrolled connection id or routing pseudonym |
| envelope_class | Coarse class only: `knock`, `session` or `store_and_forward` |
| delivery_mode | `session` or `store_and_forward` |
| ciphertext_length | Byte length of the decoded ciphertext |
| issued_at | Timing: unix seconds when the packet was issued |
| ciphertext_hash | Base64url (no padding) SHA-256 of the ciphertext bytes |

Packet type (telling, delivery, acknowledgement) lives only
inside the sealed body, per the protocol contract, so a telling, a question and
a confirmation reply inside session traffic are
indistinguishable to this relay. There is no material class
field, no sender or recipient field, and no subject field.
Adding any envelope field is a contract change.

The relay checks ciphertext length and hash agreement (hashing
is not opening), and checks the signature's shape (64 bytes)
only. It never verifies a signature: verification belongs to
the endpoints, which hold the enrolled keys, and the relay
holds no keys with which it could verify anything.

## Relay timing

The snapshot, view and log add two relay generated fields,
received_at and expires_at (unix seconds). Together with
issued_at these are the relay timing fields: the regression net's
packet view asks for relay received and delivered times, and a
queue window cannot be stated or enforced without them. No
other relay generated metadata field exists.

## Forwarding rule

The envelope carries no separate destination field, so the
relay forwards a household bucket by side:

* The household node connection (registry role `node`) polls
  and collects every queued packet for its household that it
  did not post itself.
* A member connection polls and collects the queued packets
  posted by the node whose envelope connection_id is the
  member's own connection.
* Anything else stays queued until it expires.

Which connection posted a packet is kept only in relay process
memory, only to apply this rule. It is never written to the
snapshot, the log or the relay view. Packets reloaded from a
snapshot after a relay restart are treated as member posted, so
a delivery in flight across a restart can be collected by the
node and dropped there; that edge is stated here rather than
hidden. If the frozen protocol contract names a different routing
reading for connection_id, this rule is the part that changes;
the store and log shapes do not.

## Stated windows and limits

| Item | Value |
| --- | --- |
| Session queue window | 60 seconds, then dropped |
| Store-and-forward queue window | 300 seconds, then dropped |
| Largest ciphertext | 65,536 bytes (the protocol maximum) |
| Rate limit | 30 accepted packets per connection per minute |
| Replay window | A packet_id seen in the last 600 seconds is rejected as a duplicate |

## What the relay does and does not do

Does: authenticate connection standing (token hash against the
operator registry, and the registry household must match the
envelope household), enforce the limits above, expire on the
timer above, forward per the rule above and drop each packet
on forward or on expiry.

Does not: confirm a join (a knock is queued like any packet and
changes no standing), judge an audience, answer a question,
verify a signature, open a packet, hold any key, keep a record
database, or log content. Silence is never delivery: forwarding
happens only on a destination poll, and a packet that expires
is dropped, not delivered. A telling is recorded only when the
node says so, and the node is not this service.
