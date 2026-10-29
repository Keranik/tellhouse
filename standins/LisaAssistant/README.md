# Lisa reference assistant

A stand-in assistant for Lisa, a fictional member of the Tellhouse
demo household (the Ellis household in the seed scenario). Lisa is a
fictional person and this program is a reference assistant built for
the demo: it is not a real person's assistant, it holds no real
person's data, and nothing about it should be read as a real
assistant product joining the house.

What it does, and all it does:

- Generates its own single identity keypair on first run (one
  keypair for both ECDH and ECDSA, per the frozen contract, with
  domain separation carried by the contract labels) and keeps it
  in its own store directory. The private key never leaves it.
- Joins the house through the door ceremony: it knocks with the
  invite it was handed, a named inviter confirms after checking the
  check phrase, and the invite is then disposed. Only a credential
  derived from the invite stays in the store.
- Opens a session by the frozen contract's section 7 exchange
  (knock class session_open telling, session_challenge delivery,
  acknowledgement carrying the proof signature), then sends
  sealed raw text tellings through the real relay, using
  its own implementation of the contract. Scripted acts are in
  script.json. She sends session mode packets only, inside the
  node's stated policies (30 minute session window;
  store_and_forward only after a completed session open).
- Polls the relay for sealed deliveries addressed to its
  connection, opens them under its own key, and returns a protocol
  acknowledgement built from the actual receipt. The house records
  that acknowledgement as reported, not observed.
- Exposes a trigger endpoint, POST /trigger/test-send, for the
  judge-drivable Test Send control on the home base. The trigger
  takes an act id only and refuses any request that tries to carry
  telling content.

What it never does:

- It has no reference to the home node's data layer, no shared
  database access, and no shared Tellhouse library performing its
  protocol work. The only shared artifacts are the frozen contract
  and its shared test vectors.
- It never confirms a record event on the node's behalf.
- If a sealed packet does not open under its key, the packet is
  refused whole. There is no plaintext fallback.
- It has no route that returns packet bodies. Its HTTP surface is a
  label page, a status view of metadata, the trigger, and the join
  and poll actions.

Run: set STANDIN_STORE, RELAY_URL, NODE_URL, and ASPNETCORE_URLS,
then run the project. A pending invite file (pending-invite.json
with code, check_phrase, and household_endpoint) must be placed in
the store by the inviter before the join actions run.

Selftest: run the built binary with the argument "selftest" and
VECTORS_PATH pointing at the shared vectors file. It checks the
canonical serialization, the ciphertext hash, opening under the
vector key, refusal under a wrong key, a seal and open round trip,
and that the same plaintext seals differently twice.
