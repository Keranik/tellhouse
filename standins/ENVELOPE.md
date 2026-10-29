# Stand-in envelope format: reconciled to the frozen contract

Status: SUPERSEDED as a draft. The governing format is the
Tellhouse Protocol Contract version 1.0 (frozen), in
src/Tellhouse.Protocol/CONTRACT.md, with the shared vectors at
src/Tellhouse.Protocol/vectors/test-vectors.json (a copy sits in
this folder at vectors/test-vectors.json for the stand-ins'
selftests). Both stand-ins were recoded against that contract
and those vectors. Nothing here defines its own wire format
anymore.

What the stand-ins implement, from the contract:

- Wire packet: exactly ciphertext, envelope, signature.
- Envelope: exactly the eight envelope fields (ciphertext_hash,
  ciphertext_length, connection_id, delivery_mode, envelope_class,
  household_endpoint, issued_at, packet_id). No material class,
  no packet type, no sender name in the clear.
- One identity keypair per party for both ECDH and ECDSA, with
  domain separation carried by the contract labels
  (tellhouse/seal/v1, tellhouse/sig/v1, tellhouse/session/v1).
- Sealing: ephemeral ECDH P-256, HKDF-SHA256 with the ephemeral
  public point as salt and the seal label as info, AES-256-GCM,
  authenticated data the canonical envelope without the
  ciphertext_hash field. Opening failure refuses the packet;
  there is no plaintext fallback.
- Session open per contract section 7: knock class session_open
  telling with a client nonce, knock class session_challenge
  delivery back, session class acknowledgement carrying the
  proof signature over the contract transcript.
- Frozen scope ids: household hh-demo-01, members member-lisa
  and member-daniel, connections conn-lisa-01 and conn-daniel-01.
- Node policies the stand-ins meet (stated in the home node's
  README): a verified session window of 30 minutes, and
  store_and_forward accepted only after a completed session
  open. The stand-ins send session mode packets only.

Two implementation findings from coding against the vectors,
recorded because any other consumer of the vectors will meet
them:

- The platform ECDiffieHellman DeriveKeyMaterial call does not
  return the raw 32 byte X coordinate the contract specifies as
  the shared secret; measured against the vectors, it returns a
  further derived material, and packets sealed with it do not
  open under the vector keys. The stand-ins therefore compute
  the raw agreement directly (a P-256 scalar multiplication in
  each project's Protocol file). With that, the frozen vectors'
  ciphertext reproduces byte for byte in both stand-ins'
  selftests (46 of 46 checks each).
- In this sandbox, HTTP connections made through dual-mode
  (IPv4-mapped IPv6) sockets are reset by the network
  interception layer, while plain IPv4 sockets pass. Each
  project's HTTP client therefore forces an IPv4 socket and no
  proxy for mesh traffic. This is a transport property of the
  sandbox, not a protocol property.

Consent valve and proposals, per the home node's dispositions
(the frozen plain packet carries no valve field):

- Consent is standing node state. At processing the node maps
  packet kind to a material class (raw telling to OneLiner,
  question to Question, proposal-disposing acknowledgement to
  ProposalResponse) and requires a Consent in force for that
  class. The stand-ins carry no valve basis in band. Content
  classes are recorded on the node's Consents and cannot be
  checked in band; the stand-ins do not claim in-band class
  checking.
- A ProposalResponse rides the acknowledgement packet: the node
  sends a proposal as a sealed delivery (kind proposal), and the
  addressed member's acknowledgement disposition (confirmed or
  declined) disposes it. A counter cannot ride an
  acknowledgement; a counter is sent as a telling. Daniel's
  stand-in is shaped accordingly: plain deliveries are
  acknowledged received, proposals are disposed with his
  scripted disposition.

Relay behavior the stand-ins experience:
a member collects node-posted packets whose envelope
connection_id is its own; session queue window 60 seconds,
store-and-forward window 300 seconds, then dropped; the relay
checks shape, length, and hash agreement only and never verifies
signatures or confirms joins; the door is the node's.
