# HomeNode: the Tellhouse home node

The home node is the household's own service. It carries the
verified house layer in substance (the record and its audit, the
extraction pipeline as the sole model caller, the deterministic
audience decision, discretion on read paths, the brief and the
lookup, the plan and confirmation pipeline, the lapse, and the
five house pages served from here) and adds the federated
surface: the door, sealed intake, and the sealed delivery outbox.

It is a separate project. The house layer project in
src/HouseholdCoordinator does not reference it and it does not
reference that project; shared behavior lives in named services
used by both, not in a rewrite.

## Run

```
dotnet run                 # serves the house pages, Mock extraction by default
dotnet run -- --demo-suite # the regression harness (see below)
dotnet run -- --node-suite # the node suite (see below)
```

## Two keys, two jobs, never conflated

- The node content keypair (packet sealing) is the node's one
  P-256 identity keypair under the frozen protocol contract. It
  serves both ECDH sealing and ECDSA signing, separated by the
  contract's derivation and signature labels. It lives in the
  node keystore directory (Node:KeyStorePath config, else the
  HOME_NODE_KEYSTORE environment variable, else a node-keys
  folder beside the binary) as node-content-key.txt. Its public
  fingerprint, hex SHA-256 of the 65 byte uncompressed point, is
  what the door registry shows for the node itself.
- The household backup key (keystore) is the backup tool's symmetric
  32 byte key, used to seal backups. A backup is sealed data,
  not a packet to a reader, so it is a different mechanism from
  packet sealing on purpose. The node never derives one key
  from the other.

## The door

An invite is a knock, not a key: a one-time code plus the house
address, spent the moment a join request is submitted, expired
within a day. A knock grants nothing. The named inviter sees a
card (claimed name, claimed party id, provider labeled
self-asserted, level consequences, the claimed key's protocol
fingerprint, and a check phrase of four plain words) and
confirms only when the phrase matches what the joiner reads
back over a trusted channel. Declining is recorded as an
explicit disposal. Confirming creates one HouseConnection:
party id, the single enrolled public key, its fingerprint, and
standing. The registry stores no private material. Standing is
enforced on every packet intake: Revoked connections are
refused outright; KnockOnly connections may submit knock class
packets only.

## Sealed intake (protocol contract, frozen)

The protocol library is vendored under Protocol/ (see
Protocol/VENDORED.md) and the frozen vectors sit beside it.
Intake follows the contract's section 8 order: parse the wire
packet (exactly ciphertext, envelope, signature), parse the
envelope (exactly the eight fields), check the packet_id seen
registry before opening, recompute the ciphertext length and
hash, verify the signature under the enrolled connection key,
then open (AES-256-GCM, AAD exactly Envelope.AadBytes(); the
ciphertext_hash is bound by the signature and the recomputed
hash, not by the AAD), validate the plain packet against the
envelope and the enrollment, and enforce the packet nonce as
single use per connection. Only then does content processing
begin. Every outcome, accepted or rejected, writes one trail
line; a rejection writes no event.

A telling that passes intake is queued and marked unrecorded.
Processing opens it inside the house boundary, resolves the
speaker from the connection enrollment (never from content),
runs the same extraction and confirmation pipeline as a front
door telling with SourceType federated, and stamps each written
event with the record's own provenance fields
(ProvenanceOrigin, for example "Lisa's assistant",
ProvenanceConnection, ProvenancePacketId), plus ToldAtUtc from
the packet's told_at, RecordedAtUtc when the node took it in,
and ReportedNotObserved. Record surfaces render provenance from
the record itself (ProvenanceRendering.Describe); no packet
view is a second provenance source.

Sessions follow the contract's section 7 exchange: a knock
class session_open telling, a knock class session_challenge
delivery back, and a session class acknowledgement carrying the
proof. Challenges live 120 seconds, are single use, one
outstanding per connection. Node policy, stated here because
the contract fixes only the challenge lifetime: a verified
session stays open for 30 minutes, and a store_and_forward
packet is accepted only from a connection that has completed a
session open at least once.

## The valve, and proposals, under the frozen body shapes

The frozen plain packet carries no valve field and no proposal
fields; its body is exactly kind and text, or an
acknowledgement body. Two mappings follow, flagged in the open
as home node proposals:

- The Consent valve is standing node side state (Consent
  records: material class and content class from the closed
  lists, required expiry, created by read-back, revocable). At
  processing time the node maps the packet kind to a material
  class (raw_telling to OneLiner, question to Question,
  proposal-disposing acknowledgement to ProposalResponse) and
  requires a Consent in force for that class from the enrolled
  person. No Consent in force means the valve is closed and
  the packet is rejected whole. Content classes are recorded
  on the Consent but cannot be checked in band, because the
  frozen body shapes carry no content class signal.
- ProposalResponse rides acknowledgement packets. The node
  sends a proposal as a sealed delivery whose source is the
  proposed event or plan element; the member's acknowledgement
  disposition disposes it (confirmed confirms the event, or
  records Agreed on the element; declined declines). A counter
  cannot ride an acknowledgement; a counter is a telling.

## Sealed delivery outbox

A remote member (a person holding an Active connection) never
receives a plaintext row. Confirmations dispatch and plan
landings route through SealedDeliveryService: a protocol delivery
packet sealed to that member's enrolled key, stored as the
full wire packet with a one-way dedupe fingerprint. Members
without a connection keep the house layer's in-database
delivery, unchanged. A member's acknowledgement marks the
outbox item acknowledged and is recorded as reported, not
observed: the house knows the member's assistant said received,
not that the member read anything.

## Relay client (sealed relay contract)

RelayClientService posts wire packets to POST /packets and
polls GET /inbox as the node-role connection, which collects
every queued packet for its household that it did not post.
Poll entries arrive flat (the eight envelope fields plus
received_at, expires_at, ciphertext, signature at top level);
RebuildWireJson rebuilds the wire packet through the protocol
record so the nested envelope is canonical, and relay timing
fields are dropped. A telling is recorded only when the node
takes it in through sealed intake, never when the relay held
it.

## Adopted behavior

- Provenance: the three provenance fields on Event and
  ProvenanceRendering, adopted exactly; this intake is their
  writer. MemberDeliveryService carries the same shape,
  including per item provenance.
- Audience defaults: the audience defaults in full:
  AudienceDecisionService, the PersonAudienceDefault,
  AudienceDefaultRequest and AudienceAsk tables,
  Event.AudienceAskPending, ExtractionEvent.StatedMarks in the
  extraction contract (live prompt and mock patterns), and the
  FrontDoor defaults sections (defaults list, set-by-telling
  form with read-back, questions waiting). The audience rules
  stand as recorded: narrower wins where both defaults
  apply, the ask lapse span is 24 hours from presentation, and
  default setting is a front door telling form with read-back.

## Suites

- Regression harness: dotnet run -- --demo-suite runs the
  house layer demo suite against the node. The original 103
  check assertions are unmodified; the audience defaults added
  30 more, for 133 checks total against the node.
- Node suite: dotnet run -- --node-suite runs 54 checks: the
  frozen protocol vectors (canonical forms, hashes, signatures,
  opening, byte for byte re-sealing from the published seal
  inputs, fingerprints, the session proof), the door, the
  session exchange and its failures, intake ordering and its
  negatives, the valve both ways, proposal disposal by
  acknowledgement, per reader sealing, alert routing, the
  sealed question answer, store and forward, the relay wire
  rebuild, and standing enforcement.

No live model calls are made by either suite; extraction runs
on the mock client throughout.
