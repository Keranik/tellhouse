# Demo seed artifacts

This folder holds the artifacts the demo seed ceremony placed
here on 2026-10-29: a real door ceremony run locally against
this tree, after which the record was snapshotted by the
product's own backup tool. The image built from
`src/HomeNode/Dockerfile` copies this folder to
`/app/demo-seed/` in the image, and `/app/entrypoint.sh` uses
it on a fresh boot only: an existing database or key file is
never touched.

The three artifacts:

- `node-keys/node-content-key.txt`: the node's demo content
  keypair, produced by the ceremony run. On a fresh boot the
  entrypoint copies it into the node's keystore directory only
  when no key file exists there yet.
- `demo-snapshot.thb`: the sealed snapshot of the determined
  demo record, taken by the product's backup tool
  (TellhouseBackup). On a fresh boot the entrypoint restores
  it through the product's own reset path, TellhouseBackup
  `demo-reset`. The snapshot was verified when it was collected:
  restoring it and comparing table by table against the
  ceremony record reported every table equal.
- `demo-recovery-phrase.txt`: the demo recovery phrase for
  that snapshot. The entrypoint reads it and passes it to
  `demo-reset` as the phrase that opens the snapshot.

All of this is demo material for a fictional household, the
Ellis household, published on purpose. A deployed instance
running in fresh deploy mode (the render.yaml blueprint)
uses none of it: it generates its own keypair and recovery
phrase at first boot, so this published phrase opens only
this fictional demo record and nothing that is deployed. The phrase opens only
this fictional demo record, whose contents are also what the
public demo shows, so publishing it exposes nothing private.

A real household's key, snapshot, and phrase would never ship
anywhere. They stay with that household, and none of them
would ever be placed in an image, a repository, or any other
published artifact.
