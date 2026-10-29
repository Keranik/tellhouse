# Tellhouse Protocol Contract, version 1.0 (frozen)

This file is the contract every other project in this tree
codes against: the sealed relay, the home node, and the stand-in
assistants. The stand-ins code against this document and the
test vectors only, never against this library's internals. Other
projects may use this library as a reference implementation; the
vectors, not the library, define correctness.

Status: frozen. Any change to the wire format, the field lists,
the labels or the algorithms in this document is a contract
change: it needs a new contract version and new vectors, and it
must be flagged in the open, never applied silently.

Scope, locked: one household, two members, three packet types
(telling, delivery, acknowledgement).

## 1. Parties and keys

Each party holds one identity keypair on NIST P-256:

- the household node holds the household content keypair,
  generated on the node;
- each member holds a member keypair, generated locally by the
  member's own program; a member private key never crosses a wire.

The same keypair serves ECDH sealing and ECDSA signing. This is a
demo scale simplification, stated plainly: domain separation is
carried by the derivation and signature labels in sections 5 and
6, not by separate keys.

Encodings:

- Public key: the uncompressed SEC 1 point, 65 bytes,
  0x04 || X (32 bytes) || Y (32 bytes), base64url.
- Private key: the 32 byte scalar, base64url. Local stores and
  test vectors only.
- Fingerprint: lowercase hex SHA-256 of the 65 public key bytes.
  This is the fingerprint the door ceremony stores on the node.
- Base64url everywhere: RFC 4648 section 5, no padding.
- Party ids in this contract's examples: the node is
  household-node, the members are member-lisa and member-daniel,
  their connections are conn-lisa-01 and conn-daniel-01, and the
  household endpoint is hh-demo-01. The home node and the sealed
  relay own the real registry; these ids are the vector ids.

## 2. Canonical serialization

Every signature, hash input and seal input in this protocol is
computed over canonical bytes, defined as:

1. UTF-8 JSON, no whitespace anywhere.
2. Object keys sorted by ordinal string comparison, recursively.
3. Arrays keep their order.
4. String values are raw UTF-8 with exactly three escapes: a
   double quote is written backslash quote, a backslash is
   written as two backslashes, and a control character below
   0x20 is written as a six character unicode escape with
   lowercase hex. Shortcut escapes are never used.
5. Object keys are printable ASCII only, never a double quote or
   a backslash.
6. Numbers are integers (signed 64 bit), base 10, no leading
   zeros, no fraction or exponent.
7. Booleans are true or false; null is null.
8. Duplicate object keys are rejected.

Data that cannot be written under these rules is rejected, never
normalized. Ids (packet ids, party ids, connection ids, endpoints)
are printable ASCII, 1 to 128 characters, never a double quote or
a backslash.

## 3. The wire packet and the envelope

A wire packet is one canonical JSON object with exactly three
keys:

- ciphertext: base64url of the sealed blob (section 5);
- envelope: the envelope object below;
- signature: base64url of the packet signature (section 6).

The envelope is the complete relay visible metadata. Its fields
are exactly the relay-visible metadata list, in canonical order:

- ciphertext_hash: base64url SHA-256 of the sealed blob;
- ciphertext_length: byte length of the sealed blob;
- connection_id: the enrolled connection id or routing pseudonym;
- delivery_mode: session or store_and_forward;
- envelope_class: knock, session or store_and_forward;
- household_endpoint: routing address of the household node;
- issued_at: timing, unix seconds when the packet was issued;
- packet_id: unique id of this packet.

Nothing else is relay visible. In particular:

- there is no material class field, and there never may be one:
  a telling, a question and a confirmation reply inside session
  traffic must stay indistinguishable to the relay;
- there is no packet type field: telling, delivery and
  acknowledgement live inside the sealed body only;
- there is no sender name, subject or body derived field.

The signature is authentication material for the endpoints. The
relay forwards it without interpreting it; it carries no content
information and adds nothing to the metadata list above.

Envelope limits: ciphertext_length is at most 65536. An envelope
with any other field set, or bytes that are not the canonical
form of its fields, is rejected at parse.

Coarse classes: knock is used only for the session open exchange
(section 7). Session and store_and_forward describe carriage, and
delivery_mode repeats that choice as its own field because the
list names both the coarse class and the carriage.

## 4. The plain packet (inside the seal)

The sealed plaintext is one canonical JSON object:

- body: the type specific object below;
- connection_id: must equal the envelope connection_id;
- packet_id: must equal the envelope packet_id;
- packet_nonce: base64url of 16 random bytes, single use;
- packet_type: telling, delivery or acknowledgement;
- proof: optional, only for session open responses (section 7);
- recipient_id: the enrolled party id of the recipient;
- sender_id: the enrolled party id of the sender;
- told_at: unix seconds the sender told this. Told time is the
  sender's time; recorded time is the node's intake time, and
  the home node keeps the two distinct.

Body shapes are closed lists. Any other key is rejected:

- telling and delivery: exactly kind (a short ASCII kind word,
  for example raw_telling, delivery, session_open,
  session_challenge) and text (the raw text; structured event
  fields, proposed structure or bulk history are outside this
  protocol and rejected);
- acknowledgement: exactly acked_packet_id and disposition, one
  of received, confirmed, declined. An acknowledgement is
  recorded by the node as reported, not observed.

A recipient rejects a plain packet whose packet_id or
connection_id differs from its envelope, whose sender differs
from the connection enrollment (the home node owns enrollment), or
whose packet nonce has been used on that connection before
(section 8).

## 5. Sealing and opening

Sealed blob layout, one byte string:

- bytes 0 to 64: ephemeral public point, 65 bytes, uncompressed;
- bytes 65 to 76: AES-GCM nonce, 12 bytes;
- the middle: ciphertext, the same length as the plaintext;
- the last 16 bytes: AES-GCM tag.

Algorithm:

1. Key agreement: ECDH on P-256 between the ephemeral private
   key and the recipient public key (to open: the recipient
   private key and the ephemeral public point). The shared
   secret is the raw 32 byte X coordinate.
2. Key derivation: HKDF-SHA256, input keying material the shared
   secret, salt the 65 ephemeral public point bytes, info the
   ASCII label tellhouse/seal/v1, output 32 bytes.
3. Encryption: AES-256-GCM with that key and the 12 byte nonce.
4. Authenticated data: the canonical envelope without the
   ciphertext_hash field (the seven remaining fields, canonical
   form). The hash field cannot be inside its own authenticated
   data; it is bound by the packet signature (section 6) and by
   the intake check that recomputes it from the blob.

Directions:

- telling packets seal to the node key (the household content
  public key) and open only on the node;
- delivery packets seal to the member key and open only for that
  member;
- acknowledgement packets seal to the node key.

A fresh ephemeral keypair and a fresh nonce are generated for
every packet, so the same plaintext twice seals to different
blobs. Opening with the wrong key, under a different envelope, or
over tampered bytes fails with no plaintext produced; there is no
plaintext fallback anywhere in this protocol.

## 6. Per connection signing

Every packet is signed by its sender over the byte string:

ASCII tellhouse/sig/v1, then byte 0x0A, then the full canonical
envelope bytes, then byte 0x0A, then the sealed blob bytes.

Signature: ECDSA on P-256 over SHA-256, IEEE P1363 form (r
concatenated with s, 64 bytes), base64url. Verification uses the
enrolled public key for the packet's connection: member packets
verify under the member key, node packets under the household
content key. A packet that is unsigned, signed with a non
enrolled key, or whose envelope or blob changed after signing is
rejected before opening.

Note on determinism: .NET ECDSA signing is randomized, so
signatures in the test vectors are verification only. A consumer
verifies them; it never compares signature bytes.

## 7. Session open challenge

This challenge is the demo substitute for a challenge per packet,
disclosed as such wherever the flow is described.

1. Open request: the member sends a knock class packet, sealed to
   the node key and signed with the member key, packet type
   telling, body kind session_open, body text the client nonce
   (base64url of 16 random bytes).
2. Challenge: the node replies with a knock class delivery
   packet, sealed to the member key and signed with the node key,
   body kind session_challenge, body text the server nonce
   (base64url of 16 random bytes). A challenge lives 120 seconds
   and is single use; the node keeps at most one outstanding
   challenge per connection.
3. Response: the member sends a session class acknowledgement
   packet, sealed to the node key and signed with the member key,
   whose proof object carries client_nonce, server_nonce and
   proof_signature. The proof signature is ECDSA P-256 over
   SHA-256, P1363 form, under the member key, over the transcript:
   ASCII tellhouse/session/v1, byte 0x0A, the connection_id in
   ASCII, byte 0x0A, the client nonce bytes, byte 0x0A, the
   server nonce bytes.
4. The node verifies the proof signature, both nonces against the
   exchange it started, and the expiry. Only then is the session
   open, and only then is a first session class packet from that
   connection accepted.

## 8. Replay and intake order

An endpoint admitting a packet performs these steps in order and
rejects on the first failure, creating no event and writing one
trail line:

1. Parse the packet; reject any extra or missing wire key.
2. Parse the envelope; reject any field outside the
   relay-visible metadata list and any non canonical byte form.
3. Reject a packet_id already seen (the seen registry, in the
   reference library as SeenRegistry).
4. Check ciphertext_length and recompute ciphertext_hash from
   the blob; reject on any mismatch.
5. Verify the packet signature under the enrolled connection
   key; reject on failure.
6. Open the seal; reject on failure.
7. Validate the plain packet against the envelope; reject a
   sender that differs from the connection enrollment and a
   packet nonce already used on this connection.
8. Only now may the packet's content reach any other processing.

## 9. Test vectors

The shared vectors live at:

src/Tellhouse.Protocol/vectors/test-vectors.json

They contain the fixed test key material for the node and both
members (test only private keys, published on purpose so any
project can reproduce them), four complete packets (a telling from
Lisa to the node, a delivery from the node to Daniel, an
acknowledgement from Daniel to the node, a store and forward
delivery from the node to Lisa), and one session challenge proof
with its transcript. For each packet the file gives the envelope
object, the canonical envelope string, the ciphertext, its
SHA-256 in hex, the signature (verification only), the canonical
plaintext, the full canonical wire packet, and the seal inputs
(ephemeral keypair and nonce) so the ciphertext must reproduce
byte for byte.

Regenerate them only with:

dotnet run --project tools/Tellhouse.Protocol.VectorGen

Run the protocol checks with:

dotnet run --project tests/Tellhouse.Protocol.Tests

The check suite loads the vectors from the file and codes
against them from the outside, the same way later consumers must.

## 10. What this contract does not do

Named so nobody builds on a misunderstanding:

- No relay behavior is specified here beyond what it may see.
  Queueing, limits and expiry are for the sealed relay, against
  section 3.
- No door ceremony is specified here beyond fingerprints and the
  session challenge. Inviter cards, check phrases and disposal
  are for the home node, building on sections 1 and 7.
- No record, extraction or audience behavior is specified here.
  A packet that passes intake hands its raw text to the home
  node's existing pipeline unchanged.
- No backup or restore sealing is specified here. The backup
  tool seals with the household content key under its own design.
