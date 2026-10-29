# Sealed backup, recovery restore, demo reset

Node side project. New code only; the monolith is not modified and is
not referenced by this project. It works on the node's record database
file (SQLite, the monolith and node record format) from outside the
record code, the same place a household backup tool would sit.

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
  tables, which the announcement advances), as stated for the restore proof.
- Announcement. A restore writes one HouseholdNote event into the
  restored record (Household audience, marked RestoreAnnouncement in
  its Details, naming the backup and the restore path) and one
  EventAudits row for it with channel "restore" and a null actor.
- Demo reset. The same machinery: demo-snapshot seals the seeded
  demonstration state, demo-reset restores it. Seeded stand-in
  traffic with provenance (origin, connection, packet id in the
  event Details) is part of the snapshot and returns with the reset,
  together with the restore audit row.
- At rest file sealing for a stopped node. seal-db seals the whole
  database file under the household content key; unseal-db opens it
  and verifies the plaintext hash before writing. While sealed, the
  file on disk holds no record text.

## Commands

See the program usage output (`dotnet run --project src/TellhouseBackup`
with no arguments). Proof run: `tests/run-lane-s.sh`, which builds and
runs the selftest harness, then, when the seeded monolith database is
present in the main tree, repeats the restore proof against
that real record database.

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
   identifier is a rename.
   Open design point: confirm whether backup wrapping should use the content key
   directly as here, or a key derived from the node keypair.
3. Consent, grants and valve tables. The monolith record has no such
   tables yet; they belong to the split node. The canonical export
   is table agnostic, so those tables ride the backup automatically
   when they exist, and the selftest fixture includes them in the
   split shape (Connections, ConsentGrants, ValveSettings) to prove
   the restore covers them. That fixture is a backup tool test fixture,
   not a claim that the node tables exist today.
4. Restore governance beyond announcement (quorum, veto, sensitive
   power gating, receiving only windows) is not built and is not
   claimed. It stays direction, as designed. The restore
   audit row records a null actor; no governance row is written.
5. The demo snapshot in the selftest uses the backup tool fixture
   record as the seeded demonstration state. On the full demo, the
   snapshot is taken from the real seeded node database using the
   same two commands; the proof run in tests/run-lane-s.sh already
   runs the restore half against the real seeded database.
