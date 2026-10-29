# Demo seed registry

This directory holds one file in the built image:
`relay-config.json`.

That file was placed here by the demo seed ceremony (run on
2026-10-29 against this tree): a real door ceremony run
locally, whose result is the connection registry for the
fictional Ellis household. The registry is baked into the
image as demo material so a fresh service instance can boot
with the demo household already enrolled.

A deployed instance running in fresh deploy mode (the
render.yaml blueprint) does not use this registry: its
registry is built at deployment time from operator-supplied
hashes of fresh credentials.

The registry carries token SHA-256 hashes only. Those hashes
are of demo credentials that are already public in the repo's
test drivers. Knowing a hash here does not reveal a raw token,
and the relay itself never stores or receives a raw token in
its config.

No raw token or key ever belongs in this directory. Do not add
private keys, connection tokens, sealed packet keys, or any
other key material here, in the image, or in any other relay
file. The relay holds no keys and opens nothing.
