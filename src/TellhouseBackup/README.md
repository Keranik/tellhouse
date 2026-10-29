# TellhouseBackup: sealed backup, recovery restore, demo reset

A node side project. New code only; the household coordinator
program is not modified and is not referenced by this project. It
works on the node's record database file (SQLite, the record
format the node uses) from outside the record code, the same
place a household backup tool would sit.

## What is built

- Sealed backup. The canonical export of the record (every table,
  every row, every field, deterministic order) is sealed with a fresh
  random data key under AES-256-GCM. The data key is wrapped twice:
  under the household content key (node keystore) and under a recovery
  secret. The backup file holds ciphertext and wrapped keys only.
- Recovery key. Generated on the node at backup time, shown once as a
  household held phrase (32 words, one per secret byte, plus one
  checksum word, from the 256 word list in WordList.cs). The phrase is
  never written to disk by this tool, never stored with the node, and
  never stored in the backup. The node keeps only the wrapped data key.
- Restore onto a fresh node using only the recovery key. The restore
  code path takes no keystore argument. It opens the backup first,
  rebuilds the database into a temporary file, checks foreign keys,
  writes the restore announcement, and only then moves the file into
  place. A wrong key fails at the open step and leaves no destination
  file: no partial record is readable.
- Restore proof. Canonical exports before and after are compared
  field for field across all tables, which covers, by construction,
  the record, membership (Persons), grants and connections
  (Connections), Consent (ConsentGrants), valve settings
  (ValveSettings), and every stored audience. Stored audiences are
  also compared separately, per event id (SensitivityClass plus
  AudienceRule), including Restricted and Personal events, so a
  widening can never hide inside a general equality pass. The one
  permitted difference is the restore announcement itself (its event,
  its audit row, and the AUTOINCREMENT sequence rows for those two
  tables, which the announcement advances).
- Announcement. A restore writes one HouseholdNote event into the
  restored record (Household audience, marked RestoreAnnouncement in
  its Details, naming the backup and the restore path) and one
  EventAudits row for it with channel "restore" and a null actor.
- Demo reset. The same machinery: demo-snapshot seals the seeded
  demonstration state, demo-reset restores it. Seeded stand-in
  traffic with provenance (origin, connection, packet id in the
  event Details) is part of the snapshot and returns with the reset,
  together with the restore audit row.
- sqlite_sequence, handled exactly. The AUTOINCREMENT high water
  marks are record state and ride in the export. On restore, the
  row that inserting the data creates for each table is replaced
  by the exported row, never added to it, so a restored database
  holds exactly one sequence row per table with the exported
  value. That matters when the mark sits above the largest
  surviving id (tail rows deleted before the backup): reading the
  wrong row would hand deleted ids out again. The comparison
  used by verify and by the selftest compares row counts per
  table and rows as multisets (a row occurring twice is a
  difference from the same row occurring once), including
  sqlite_sequence, so "equal" means equal. The one permitted
  sequence difference remains the restore announcement, which
  advances the Events and EventAudits marks by one each; the
  selftest also reads the restored sequence directly and checks
  every other table's mark against the exported value.
- Restore is a stopped node operation, and it removes stale
  journal files. Never run restore or demo-reset onto a database
  a running node has open: the tool cannot stop the node for you,
  and a running node keeps working on the file it opened. Stop
  the node first (the packaged flows do: the entrypoint restores
  before the app starts, and DEPLOY.md's reset sequence stops
  the service, restores, and starts it again). When the restore
  swaps the restored file into place it first deletes the
  destination's journal sidecar files (the -wal, -shm, and
  -journal files). Those files belong to the old database file;
  left in place, the next open can replay them against the
  restored file and produce a mixed record. The selftest proves
  the removal against sidecars copied from a live WAL database
  that had gained a row after the backup.
- Further parts of that stopped node rule. Before the swap the
  restore tool now refuses a destination another process holds
  open (visible on Linux through /proc; where the holder lives
  in another container the stop first rule above still applies),
  and it checkpoints the destination's WAL, refusing when the
  checkpoint stays busy. It also never
  rolls back the seen packet tables: rows in PacketSeen and
  PacketNonceUses that the destination accepted after the
  snapshot was taken are merged into the restored database, so
  a packet the reset removed from the record cannot be recorded
  a second time when it is re-posted after the relay's short
  in-memory duplicate window passes. A demo reset therefore
  returns the record to its determined state while keeping
  exactly this replay protection, and nothing else, from after
  the snapshot.
- At rest file sealing for a stopped node. seal-db seals the whole
  database file under the household content key; unseal-db opens it
  and verifies the plaintext hash before writing. While sealed, the
  file on disk holds no record text. The seal is taken from the
  checkpointed database: the WAL is flushed into the main file
  first (refusing while another process holds the file open or
  the checkpoint stays busy), so rows that existed only in the
  write-ahead log are sealed too, not silently left behind.

## Commands

See the program usage output (`dotnet run --project src/TellhouseBackup`
with no arguments). Proof run: `tests/run-lane-s.sh`, which builds and
runs the selftest harness, then, when a seeded record database is
present in this tree, repeats the restore proof against that real
record database.

## Divergences and limits, flagged not hidden

1. Live database at rest sealing, in the transparent sense (the node
   running against an encrypted database file), cannot be built
   honestly on this stack. The stack is EF Core on plain SQLite
   (SQLitePCLRaw e_sqlite3); there is no SQLCipher or SEE provider in
   the local package set, and hand rolling page level encryption
   under EF Core would change the database format and could not be
   verified to the standard a claim needs. Smallest honest
   alternative, built and tested here as a proposal: whole file
   sealing while the node is stopped (seal-db and unseal-db above),
   which makes the at rest file ciphertext and is verifiable by
   canary inspection, while the running node still holds a plain
   database file inside its own boundary. Adopting SQLCipher later
   replaces this proposal; nothing here claims the live form.
2. Household content key shape. The protocol designs the household
   content key as a keypair. This tool models the backup sealing
   half as a symmetric 32 byte key in a node keystore file, since a
   backup is sealed data rather than a packet addressed to a reader.
   The backup header carries the keyId, so mapping to the protocol
   identifier for the content key is a rename. One point to confirm
   when the protocol contract is applied here: whether backup
   wrapping should use the content key directly as here, or a key
   derived from the node keypair.
3. Consent, grants and valve tables. The household coordinator
   record has no such tables yet; they belong to the node. The
   canonical export is table agnostic, so those tables ride the
   backup automatically when they exist, and the selftest fixture
   includes them in the node shape (Connections, ConsentGrants,
   ValveSettings) to prove the restore covers them. That fixture
   is a test fixture for this tool, not a claim that the node
   tables exist today.
4. Restore governance beyond announcement (quorum, veto, sensitive
   power gating, receiving only windows) is not built and is not
   claimed. It remains a direction for future work. The restore
   audit row records a null actor; no governance row is written.
5. The demo snapshot in the selftest uses this tool's fixture
   record as the seeded demonstration state. In the full demo, the
   snapshot is taken from the real seeded node database using the
   same two commands; the proof run in tests/run-lane-s.sh already
   runs the restore half against the real seeded database.
