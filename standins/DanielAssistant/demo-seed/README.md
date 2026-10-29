# Demo seed store

The store snapshot for this stand-in (keys.json and state.json)
was placed under demo-seed/store/ by the demo seed ceremony
(run on 2026-10-29 against this tree): a real door ceremony
(knock, inviter confirm with check phrase, invite disposed)
run locally, whose resulting joined store is baked as the demo
starting state.

These are demo keys for a fictional person, published on purpose,
like the shared test vectors. They protect nothing real. A
deployed instance running in fresh deploy mode (the render.yaml
blueprint) never receives this store: it generates its own
keypair and joins with a fresh invite.

The join path in the program is unchanged and remains the real
one for any non-demo use. A store that already exists at runtime
is the assistant's own and is never reseeded from this snapshot;
see entrypoint.sh for the seed rule.
