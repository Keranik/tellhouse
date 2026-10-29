# Vendored protocol core (frozen)

The .cs files in this folder are the protocol reference
implementation, copied verbatim from src/Tellhouse.Protocol with
only the namespace changed from Tellhouse.Protocol to
HomeNode.Protocol, so this project stays self-contained. The
contract is the protocol's CONTRACT.md; the shared test vectors
are test-vectors.json beside this file.

Correctness is defined by the contract and the vectors, not by
this copy. If this vendored copy is later replaced
with a project reference to the protocol's library, nothing in the home node
should need to change: the home node code calls only the contract level
operations (Packet.Create, Packet.Parse, Packet.Open,
Sealer.Seal, Envelope.AadBytes as the seal authenticated data,
SessionChallenge proof build and verify, the fingerprint rule).

Terminology used across the home node, kept distinct on purpose:

- Node content keypair (packet sealing): the node's one P-256
  identity keypair. Telling and acknowledgement packets seal to
  its public half; delivery packets are signed with it. This is
  the only keypair the node uses for packets.
- Household backup key (keystore): the backup tool's symmetric keystore
  key for backup sealing. A backup is sealed data, not a packet
  to a reader, so backup sealing does not derive from the node
  content keypair, and no home node surface calls the backup key a
  content key or the content keypair a backup key.
