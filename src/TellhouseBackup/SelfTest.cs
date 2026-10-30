using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace TellhouseBackup;

// Backup tool proof harness. Builds a fixture node database in the
// node record shape (Households, Persons, Events, EventAudits) plus
// the membership, grant, Consent and valve tables the node adds
// (Connections, ConsentGrants, ValveSettings), then runs the restore
// proof: field for field comparison, stored audience equality, wrong
// key failure with no partial record, canary absence from the sealed
// bytes, demo reset through the same machinery, and at rest file
// sealing for a stopped node.
public static class SelfTest
{
    private const string Canary = "zqxjv canary turnip lantern phrase";

    public static int Run(string[] args)
    {
        var work = args.Length > 2 && args[1] == "--workdir" ? args[2]
            : Path.Combine(Path.GetTempPath(), "tellhouse-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? " :: " + detail : "")); }
            else { fail++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? " :: " + detail : "")); }
        }
        bool Throws(Action a) { try { a(); return false; } catch { return true; } }

        var db = Path.Combine(work, "node.db");
        BuildFixture(db);
        var keystorePath = Path.Combine(work, "keystore", "content-key.json");
        var ks = ContentKeyStore.LoadOrCreate(keystorePath);

        // The keystore holds the household backup key, so it is
        // created owner read/write only (0600), and a keystore file
        // an older build left readable is tightened on load.
        if (!OperatingSystem.IsWindows())
        {
            Check("keystore-file-created-owner-only-0600",
                File.GetUnixFileMode(keystorePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "mode=" + Convert.ToString((int)File.GetUnixFileMode(keystorePath), 8));
            var loosePath = Path.Combine(work, "keystore-loose", "content-key.json");
            Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
            File.Copy(keystorePath, loosePath);
            File.SetUnixFileMode(loosePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            _ = ContentKeyStore.LoadOrCreate(loosePath);
            Check("keystore-file-left-readable-is-tightened-to-0600-on-load",
                File.GetUnixFileMode(loosePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "mode=" + Convert.ToString((int)File.GetUnixFileMode(loosePath), 8));
            // The open-check path reads through Load, not
            // LoadOrCreate, and it must tighten the same way: a
            // loosened keystore used to survive every backup
            // verification at 0644 because Load read it as found.
            var looseLoadPath = Path.Combine(work, "keystore-loose-load", "content-key.json");
            Directory.CreateDirectory(Path.GetDirectoryName(looseLoadPath)!);
            File.Copy(keystorePath, looseLoadPath);
            File.SetUnixFileMode(looseLoadPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var loadedLoose = ContentKeyStore.Load(looseLoadPath);
            Check("keystore-load-path-tightens-a-loosened-keystore-to-0600",
                File.GetUnixFileMode(looseLoadPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                && loadedLoose.KeyBase64 == ks.KeyBase64,
                "mode=" + Convert.ToString((int)File.GetUnixFileMode(looseLoadPath), 8));
        }

        // 1. Word list integrity and phrase round trip.
        bool wl = true; try { WordList.Validate(); } catch { wl = false; }
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Check("wordlist-256-distinct-and-phrase-roundtrip", wl && WordList.Decode(WordList.Encode(secret)).SequenceEqual(secret));
        var tamperedPhraseWords = WordList.Encode(secret).Split(' ');
        tamperedPhraseWords[^1] = tamperedPhraseWords[^1] == "apple" ? "apron" : "apple";
        Check("phrase-checksum-word-rejects-a-swapped-word", Throws(() => WordList.Decode(string.Join(" ", tamperedPhraseWords))));

        // Backup.
        var backupPath = Path.Combine(work, "record.thbackup");
        var (backup, phrase) = SealedBackup.Create(db, ks, "record");
        SealedBackup.Write(backup, backupPath);
        var backupText = File.ReadAllText(backupPath);
        Check("sealed-backup-holds-no-canary-body-or-name",
            !backupText.Contains(Canary) && !backupText.Contains("canary") && !backupText.Contains("Margaret") && !backupText.Contains("swollen"),
            "backup bytes inspected as text");
        Check("recovery-phrase-is-not-stored-in-the-backup-or-the-keystore",
            !backupText.Contains(phrase) && !File.ReadAllText(keystorePath).Contains(phrase.Split(' ')[0] + " " + phrase.Split(' ')[1]));

        var before = CanonicalExport.Build(db);
        Check("canonical-export-is-deterministic",
            CanonicalExport.Serialize(CanonicalExport.Build(db)) == CanonicalExport.Serialize(before));

        // Opens.
        Check("open-with-household-content-key-matches-original-export",
            CanonicalExport.Serialize(SealedBackup.OpenWithContentKey(SealedBackup.Read(backupPath), ks)) == CanonicalExport.Serialize(before));
        Check("open-with-recovery-phrase-matches-original-export",
            CanonicalExport.Serialize(SealedBackup.OpenWithPhrase(SealedBackup.Read(backupPath), phrase)) == CanonicalExport.Serialize(before));
        var otherKs = ContentKeyStore.LoadOrCreate(Path.Combine(work, "other-keystore", "content-key.json"));
        Check("open-with-a-different-content-key-fails",
            Throws(() => SealedBackup.OpenWithContentKey(SealedBackup.Read(backupPath), otherKs)));
        Check("backup-alone-with-no-key-opens-nothing",
            Throws(() => Crypto.Open(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), SealedBackup.Read(backupPath).Payload)));

        // Tamper.
        var tampered = SealedBackup.Read(backupPath);
        var chars = tampered.Payload.Ciphertext.ToCharArray();
        chars[10] = chars[10] == 'A' ? 'B' : 'A';
        tampered.Payload = tampered.Payload with { Ciphertext = new string(chars) };
        Check("tampered-backup-ciphertext-fails-to-open", Throws(() => SealedBackup.OpenWithPhrase(tampered, phrase)));

        // Wrong key restore: complete failure, no destination file.
        var wrongPhrase = WordList.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var wrongDest = Path.Combine(work, "fresh-wrong", "node.db");
        Check("wrong-key-restore-fails-completely-with-no-partial-record",
            Throws(() => Program.RestoreFromPhrase(backupPath, wrongPhrase, wrongDest, "recovery-key")) && !File.Exists(wrongDest));

        // Right key restore onto a fresh node. The restore path takes no
        // keystore argument at all, which is the code level statement of
        // "recovery key alone": there is no node key to consult.
        var freshDest = Path.Combine(work, "fresh-node", "node.db");
        Program.RestoreFromPhrase(backupPath, phrase, freshDest, "recovery-key");
        var after = CanonicalExport.Build(freshDest);
        var cmp = CanonicalExport.Compare(before, after);
        Check("restore-field-for-field-equal", cmp.Equal, $"tables={cmp.TablesCompared} rows={cmp.RowsCompared}" + (cmp.Equal ? "" : " diffs: " + string.Join(" ; ", cmp.Diffs.Take(5))));
        Check("restore-audiences-equal-and-never-widened", cmp.AudienceDiffs.Count == 0 && cmp.AudienceRowsCompared >= 5,
            $"audience rows compared={cmp.AudienceRowsCompared}, Restricted and Personal events included");
        var membershipTables = new[] { "Persons", "Connections", "ConsentGrants", "ValveSettings" };
        bool membershipOk = membershipTables.All(n =>
            before.Tables.First(t => t.Name == n).Rows.Count == after.Tables.First(t => t.Name == n).Rows.Count);
        Check("restore-membership-grants-consent-and-valve-settings-return", membershipOk,
            string.Join(", ", membershipTables.Select(n => n + "=" + after.Tables.First(t => t.Name == n).Rows.Count)));

        // Announcement and audit row.
        using (var conn = new SqliteConnection($"Data Source={freshDest};Mode=ReadOnly"))
        {
            conn.Open();
            long annCount = ScalarLong(conn, "SELECT COUNT(*) FROM Events WHERE Details LIKE '%RestoreAnnouncement%'");
            long auditCount = ScalarLong(conn, "SELECT COUNT(*) FROM EventAudits WHERE Channel='restore'");
            string annClass = ScalarText(conn, "SELECT SensitivityClass FROM Events WHERE Details LIKE '%RestoreAnnouncement%' LIMIT 1");
            Check("restore-announced-in-record-with-audit-row", annCount == 1 && auditCount == 1 && annClass == "Household",
                $"announcement events={annCount} restore audit rows={auditCount} announcement audience class={annClass}");
        }

        // The announcement's stored timestamp. The
        // record's own rows are stored by the app in the text form
        // "yyyy-MM-dd HH:mm:ss" with a space separator, and the
        // timeline orders by that stored text. A round trip ("o")
        // form string carries a "T" separator and sort order above
        // every same day row no matter its clock time, so the
        // announcement must be written in the app's own form.
        using (var conn = new SqliteConnection($"Data Source={freshDest};Mode=ReadOnly"))
        {
            conn.Open();
            string annAt = ScalarText(conn, "SELECT CreatedAtUtc FROM Events WHERE Details LIKE '%RestoreAnnouncement%' LIMIT 1");
            bool o2ok = false;
            if (!annAt.Contains("T"))
            {
                try
                {
                    var annTime = DateTime.ParseExact(annAt, "yyyy-MM-dd HH:mm:ss.FFFFFFF",
                        System.Globalization.CultureInfo.InvariantCulture);
                    string twoLater = annTime.AddMinutes(2).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF",
                        System.Globalization.CultureInfo.InvariantCulture);
                    o2ok = string.CompareOrdinal(annAt, twoLater) < 0;
                }
                catch { o2ok = false; }
            }
            Check("restore-announcement-timestamp-uses-the-app-text-format-and-sorts-in-time-order", o2ok,
                $"announcement CreatedAtUtc stored as: {annAt}");
        }

        // sqlite_sequence state after restore, read raw (not through the
        // compare, whose only permitted sequence difference is the
        // announcement): exactly one row per table, carrying the
        // exported value, with Events and EventAudits advanced by
        // exactly one for the announcement event and its audit row.
        using (var conn = new SqliteConnection($"Data Source={freshDest};Mode=ReadOnly"))
        {
            conn.Open();
            var beforeSeq = before.Tables.First(t => t.Name == "sqlite_sequence");
            int nameIdx = beforeSeq.Columns.IndexOf("name");
            int seqIdx = beforeSeq.Columns.IndexOf("seq");
            var expectedSeq = beforeSeq.Rows.ToDictionary(r => r[nameIdx].V ?? "", r => long.Parse(r[seqIdx].V!));
            var actualSeq = new Dictionary<string, (long Count, long Seq)>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, COUNT(*), MAX(seq) FROM sqlite_sequence GROUP BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) actualSeq[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            bool seqOk = actualSeq.Count == expectedSeq.Count && expectedSeq.All(kv =>
                actualSeq.TryGetValue(kv.Key, out var a) && a.Count == 1 &&
                a.Seq == (kv.Key is "Events" or "EventAudits" ? kv.Value + 1 : kv.Value));
            Check("restore-sqlite-sequence-one-row-per-table-with-exported-values", seqOk,
                string.Join(", ", actualSeq.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.Seq + (kv.Value.Count > 1 ? "(x" + kv.Value.Count + ")" : ""))));
        }

        // The compare itself must treat a duplicated row as a
        // difference. These two checks build exports that differ only
        // by one duplicated row, in sqlite_sequence and in a plain
        // table; a set based comparison reports both as equal.
        string SeqExport(string rowsJson) =>
            "{\"Format\":\"tellhouse-canonical-export\",\"Version\":1,\"Schema\":[],\"Tables\":[{\"Name\":\"sqlite_sequence\",\"Columns\":[\"name\",\"seq\"],\"Rows\":[" + rowsJson + "]}]}";
        const string oneSeqRow = "[{\"T\":\"s\",\"V\":\"Widgets\"},{\"T\":\"i\",\"V\":\"5\"}]";
        var dupSeqCmp = CanonicalExport.Compare(
            CanonicalExport.Parse(SeqExport(oneSeqRow)),
            CanonicalExport.Parse(SeqExport(oneSeqRow + "," + oneSeqRow)));
        Check("compare-reports-duplicated-sqlite-sequence-rows-as-not-equal",
            !dupSeqCmp.Equal && dupSeqCmp.Diffs.Any(d => d.Contains("sqlite_sequence")),
            string.Join(" ; ", dupSeqCmp.Diffs.Take(3)));
        string WidgetExport(string rowsJson) =>
            "{\"Format\":\"tellhouse-canonical-export\",\"Version\":1,\"Schema\":[],\"Tables\":[{\"Name\":\"Widgets\",\"Columns\":[\"Id\",\"Name\"],\"Rows\":[" + rowsJson + "]}]}";
        const string oneWidgetRow = "[{\"T\":\"i\",\"V\":\"1\"},{\"T\":\"s\",\"V\":\"a\"}]";
        var dupWidgetCmp = CanonicalExport.Compare(
            CanonicalExport.Parse(WidgetExport(oneWidgetRow)),
            CanonicalExport.Parse(WidgetExport(oneWidgetRow + "," + oneWidgetRow)));
        Check("compare-reports-duplicated-table-rows-as-not-equal",
            !dupWidgetCmp.Equal && dupWidgetCmp.Diffs.Any(d => d.Contains("Widgets") && d.Contains("row count")),
            string.Join(" ; ", dupWidgetCmp.Diffs.Take(3)));

        // High water mark above the largest surviving id: rows 4 and 5
        // were deleted before the backup, so the sequence says 5 while
        // the largest id in the table is 3. A restore that loses that
        // mark hands id 4 out again; a correct restore hands out 6.
        var hwDb = Path.Combine(work, "highwater.db");
        using (var conn = new SqliteConnection($"Data Source={hwDb}"))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE Widgets (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)");
            Exec(conn, "INSERT INTO Widgets (Id, Name) VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d'),(5,'e')");
            Exec(conn, "DELETE FROM Widgets WHERE Id>=4");
        }
        var (hwBackup, hwPhrase) = SealedBackup.Create(hwDb, ks, "record");
        var hwBackupPath = Path.Combine(work, "highwater.thbackup");
        SealedBackup.Write(hwBackup, hwBackupPath);
        var hwDest = Path.Combine(work, "highwater-restored.db");
        Program.RestoreFromPhrase(hwBackupPath, hwPhrase, hwDest, "recovery-key");
        long hwSeqRows, hwSeq, hwNewId;
        using (var conn = new SqliteConnection($"Data Source={hwDest}"))
        {
            conn.Open();
            hwSeqRows = ScalarLong(conn, "SELECT COUNT(*) FROM sqlite_sequence WHERE name='Widgets'");
            hwSeq = ScalarLong(conn, "SELECT seq FROM sqlite_sequence WHERE name='Widgets'");
            Exec(conn, "INSERT INTO Widgets (Name) VALUES ('new')");
            hwNewId = ScalarLong(conn, "SELECT last_insert_rowid()");
        }
        Check("restore-preserves-autoincrement-high-water-mark-above-max-id",
            hwSeqRows == 1 && hwSeq == 5 && hwNewId == 6,
            $"sequence rows={hwSeqRows} seq={hwSeq} first new id after restore={hwNewId} (source max surviving id was 3, high water mark 5)");

        // Stale WAL sidecars at the destination. The sidecar files
        // below are the live journal files of a WAL mode copy of the
        // same record that gained a junk person after the backup, the
        // files a stopped or crashed node leaves next to its database.
        // A restore that leaves them in place lets the next open
        // consult them against the restored file; in testing that
        // produced a mixed record (ConsentGrants holding Persons rows,
        // integrity_check failing). The restore must remove them.
        var s7Dest = Path.Combine(work, "stale-wal.db");
        File.Copy(db, s7Dest);
        var walSource = Path.Combine(work, "wal-source.db");
        File.Copy(db, walSource);
        using (var conn = new SqliteConnection($"Data Source={walSource}"))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "INSERT INTO Persons (Id, HouseholdId, DisplayName, Role, MinorFlag) VALUES (99,1,'Live Junk','Family',0)");
            File.Copy(walSource + "-wal", s7Dest + "-wal", true);
            if (File.Exists(walSource + "-shm")) File.Copy(walSource + "-shm", s7Dest + "-shm", true);
        }
        bool hadSidecars = File.Exists(s7Dest + "-wal");
        Program.RestoreFromPhrase(backupPath, phrase, s7Dest, "demo-reset");
        Check("restore-removes-stale-wal-and-shm-sidecars-of-destination",
            hadSidecars && !File.Exists(s7Dest + "-wal") && !File.Exists(s7Dest + "-shm") && !File.Exists(s7Dest + "-journal"),
            $"stale wal present before restore={hadSidecars}, wal after={File.Exists(s7Dest + "-wal")}, shm after={File.Exists(s7Dest + "-shm")}");
        using (var conn = new SqliteConnection($"Data Source={s7Dest};Mode=ReadOnly"))
        {
            conn.Open();
            long junk = ScalarLong(conn, "SELECT COUNT(*) FROM Persons WHERE Id=99");
            long grants = ScalarLong(conn, "SELECT COUNT(*) FROM ConsentGrants");
            string grantClasses = ScalarText(conn, "SELECT GROUP_CONCAT(MaterialClass, ',') FROM (SELECT MaterialClass FROM ConsentGrants ORDER BY Id)");
            string integrity = ScalarText(conn, "PRAGMA integrity_check");
            Check("restore-with-stale-wal-returns-unmixed-record",
                junk == 0 && grants == 2 && grantClasses == "HouseholdNote,HealthMention" && integrity == "ok",
                $"junk persons={junk} consent grants={grants} grant classes={grantClasses} integrity={integrity}");
        }

        // seal-db must seal the checkpointed database, not just
        // the main file. The row below lives only in the WAL
        // while the first connection stays open; a seal that reads
        // the main file alone holds none of it.
        var walDb = Path.Combine(work, "wal-only.db");
        var walSealed = Path.Combine(work, "wal-only.thsealed");
        var walDest = Path.Combine(work, "wal-only-unsealed.db");
        using (var conn = new SqliteConnection($"Data Source={walDb}"))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "CREATE TABLE WalRows (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
            Exec(conn, "INSERT INTO WalRows (Id, Name) VALUES (1, 'wal-row')");
            AtRestFileSeal.SealFile(walDb, walSealed, ks);
            AtRestFileSeal.UnsealFile(walSealed, walDest, ks);
        }
        using (var conn = new SqliteConnection($"Data Source={walDest};Mode=ReadOnly"))
        {
            conn.Open();
            string walName = "";
            try { walName = ScalarText(conn, "SELECT Name FROM WalRows WHERE Id=1"); } catch { walName = ""; }
            Check("seal-db-seals-the-checkpointed-database-including-rows-only-in-the-wal",
                walName == "wal-row", $"unsealed row name='{walName}'");
        }

        // A restore must not roll back the seen packet tables.
        // The destination below accepted a packet after the
        // snapshot was taken; its seen row and nonce row are the
        // only record that the packet was already processed, and a
        // reset that drops them lets the same packet be recorded a
        // second time when it is re-posted.
        var s8Dest = Path.Combine(work, "seen-dest.db");
        File.Copy(db, s8Dest);
        using (var conn = new SqliteConnection($"Data Source={s8Dest}"))
        {
            conn.Open();
            Exec(conn, "INSERT INTO PacketSeen (PacketId, ConnectionId, SeenAtUtc) VALUES ('pkt-after-snapshot', 'conn-lisa-01', '2026-10-28T10:00:00Z')");
            Exec(conn, "INSERT INTO PacketNonceUses (ConnectionId, PacketNonce, PacketId, UsedAtUtc) VALUES ('conn-lisa-01', 'nonce-after-snapshot', 'pkt-after-snapshot', '2026-10-28T10:00:00Z')");
        }
        Program.RestoreFromPhrase(backupPath, phrase, s8Dest, "demo-reset");
        using (var conn = new SqliteConnection($"Data Source={s8Dest};Mode=ReadOnly"))
        {
            conn.Open();
            long seenBefore = ScalarLong(conn, "SELECT COUNT(*) FROM PacketSeen WHERE PacketId='pkt-before-snapshot'");
            long seenAfter = ScalarLong(conn, "SELECT COUNT(*) FROM PacketSeen WHERE PacketId='pkt-after-snapshot'");
            long nonceBefore = ScalarLong(conn, "SELECT COUNT(*) FROM PacketNonceUses WHERE PacketNonce='nonce-before-snapshot'");
            long nonceAfter = ScalarLong(conn, "SELECT COUNT(*) FROM PacketNonceUses WHERE PacketNonce='nonce-after-snapshot'");
            Check("restore-preserves-seen-packets-and-nonces-accepted-after-the-snapshot",
                seenBefore == 1 && seenAfter == 1 && nonceBefore == 1 && nonceAfter == 1,
                $"seen before={seenBefore} after={seenAfter}, nonces before={nonceBefore} after={nonceAfter}");
        }

        // A restore onto a database another process holds open
        // must refuse and leave the destination untouched. A
        // file swap under a live holder reports success while the
        // holder keeps working on the old file and its later writes
        // are lost or resurface unpredictably. The holder below is
        // a separate python3 process holding a WAL connection open.
        var liveDb = Path.Combine(work, "live.db");
        File.Copy(db, liveDb);
        var liveReady = Path.Combine(work, "live-ready.txt");
        var liveRelease = Path.Combine(work, "live-release.txt");
        var holderScript = "import sqlite3,os,time,sys\n"
            + "c=sqlite3.connect(sys.argv[1])\n"
            + "c.execute('PRAGMA journal_mode=WAL')\n"
            + "c.execute(\"INSERT INTO Persons (Id, HouseholdId, DisplayName, Role, MinorFlag) VALUES (98,1,'Live Holder','Family',0)\")\n"
            + "c.commit()\n"
            + "open(sys.argv[2],'w').write('ready')\n"
            + "while not os.path.exists(sys.argv[3]): time.sleep(0.1)\n";
        var holderInfo = new ProcessStartInfo { FileName = "python3", UseShellExecute = false };
        holderInfo.ArgumentList.Add("-c");
        holderInfo.ArgumentList.Add(holderScript);
        holderInfo.ArgumentList.Add(liveDb);
        holderInfo.ArgumentList.Add(liveReady);
        holderInfo.ArgumentList.Add(liveRelease);
        using (var holder = Process.Start(holderInfo))
        {
            var readyDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(liveReady) && DateTime.UtcNow < readyDeadline && holder is { HasExited: false })
                System.Threading.Thread.Sleep(100);
            bool liveRefused = Throws(() => Program.RestoreFromPhrase(backupPath, phrase, liveDb, "demo-reset"));
            File.WriteAllText(liveRelease, "go");
            holder?.WaitForExit(10000);
            long holderPerson = -1, liveAnnouncements = -1;
            try
            {
                using var conn = new SqliteConnection($"Data Source={liveDb};Mode=ReadOnly");
                conn.Open();
                holderPerson = ScalarLong(conn, "SELECT COUNT(*) FROM Persons WHERE Id=98");
                liveAnnouncements = ScalarLong(conn, "SELECT COUNT(*) FROM Events WHERE Details LIKE '%RestoreAnnouncement%'");
            }
            catch { }
            Check("restore-refuses-while-another-process-holds-the-database-open-and-leaves-it-untouched",
                File.Exists(liveReady) && liveRefused && holderPerson == 1 && liveAnnouncements == 0,
                $"holder ready={File.Exists(liveReady)} refused={liveRefused} holder person rows={holderPerson} restore announcements={liveAnnouncements}");
        }

        // The liveness lock is the reset protection mechanism.
        // Lock files in the node's own format are planted beside a
        // destination database; no real node runs here, which is the
        // point: the guard reads the artifact, not the process, so
        // it works the same when the node lives in another container
        // sharing the database directory.
        void PlantLock(string targetDb, DateTime heartbeatUtc, string instance)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var payload = "{\"format\":\"tellhouse-node-liveness\",\"version\":1,\"instanceId\":\"" + instance
                + "\",\"processId\":4242,\"machineName\":\"selftest\",\"startedAtUtc\":\""
                + heartbeatUtc.AddMinutes(-5).ToString("O", inv)
                + "\",\"heartbeatAtUtc\":\"" + heartbeatUtc.ToString("O", inv) + "\"}";
            File.WriteAllText(NodeLiveness.LockPathFor(targetDb), payload);
        }

        var q42Dest = Path.Combine(work, "q42.db");
        File.Copy(db, q42Dest);
        var q42Before = Crypto.Sha256Hex(File.ReadAllBytes(q42Dest));
        PlantLock(q42Dest, DateTime.UtcNow, "selftest-live-instance");
        bool q42Refused = Throws(() => Program.RestoreFromPhrase(backupPath, phrase, q42Dest, "demo-reset"));
        Check("live-node-lock-refuses-reset-and-leaves-the-database-untouched",
            q42Refused && Crypto.Sha256Hex(File.ReadAllBytes(q42Dest)) == q42Before,
            $"refused={q42Refused}");
        bool q42OverrideOk = !Throws(() => Program.RestoreFromPhrase(backupPath, phrase, q42Dest, "demo-reset", true));
        var q42AuditPath = NodeLiveness.AuditLogPathFor(q42Dest);
        var q42Audit = File.Exists(q42AuditPath) ? File.ReadAllText(q42AuditPath) : "";
        Check("live-node-override-resets-and-logs-the-override",
            q42OverrideOk && q42Audit.Contains("override-live-node") && q42Audit.Contains("selftest-live-instance"),
            $"override restored={q42OverrideOk} audit recorded={q42Audit.Contains("selftest-live-instance")}");
        PlantLock(q42Dest, DateTime.UtcNow.AddMinutes(-10), "selftest-stale-instance");
        bool q42StaleOk = !Throws(() => Program.RestoreFromPhrase(backupPath, phrase, q42Dest, "demo-reset"));
        Check("stale-lock-lets-the-reset-proceed-and-is-removed",
            q42StaleOk && !File.Exists(NodeLiveness.LockPathFor(q42Dest)),
            $"restored={q42StaleOk} lock still present={File.Exists(NodeLiveness.LockPathFor(q42Dest))}");
        File.WriteAllText(NodeLiveness.LockPathFor(q42Dest), "this is not a lock file {{{");
        bool q42CorruptOk = !Throws(() => Program.RestoreFromPhrase(backupPath, phrase, q42Dest, "demo-reset"));
        Check("unreadable-lock-never-blocks-a-reset", q42CorruptOk, $"restored={q42CorruptOk}");
        try { File.Delete(NodeLiveness.LockPathFor(q42Dest)); } catch { }

        // The at-rest unseal swap takes the same refusal, and the
        // same logged override.
        var q42SealDbPath = Path.Combine(work, "q42-seal.db");
        File.Copy(db, q42SealDbPath);
        var q42SealedPath = Path.Combine(work, "q42-seal.db.thsealed");
        AtRestFileSeal.SealFile(q42SealDbPath, q42SealedPath, ks);
        File.Delete(q42SealDbPath);
        PlantLock(q42SealDbPath, DateTime.UtcNow, "selftest-live-instance");
        bool q42UnsealRefused = Throws(() => AtRestFileSeal.UnsealFile(q42SealedPath, q42SealDbPath, ks));
        Check("live-node-lock-refuses-at-rest-unseal",
            q42UnsealRefused && !File.Exists(q42SealDbPath),
            $"refused={q42UnsealRefused} destination written={File.Exists(q42SealDbPath)}");
        bool q42UnsealOverrideOk = !Throws(() => AtRestFileSeal.UnsealFile(q42SealedPath, q42SealDbPath, ks, true));
        var q42SealAudit = File.Exists(NodeLiveness.AuditLogPathFor(q42SealDbPath))
            ? File.ReadAllText(NodeLiveness.AuditLogPathFor(q42SealDbPath)) : "";
        Check("live-node-override-unseals-and-logs-the-override",
            q42UnsealOverrideOk && File.Exists(q42SealDbPath) && q42SealAudit.Contains("at-rest unseal"),
            $"unsealed={q42UnsealOverrideOk} destination exists={File.Exists(q42SealDbPath)}");

        // Demo reset through the same machinery. The fixture record IS
        // the seeded demonstration state for this proof: it carries two
        // federated stand-in events with provenance in their Details.
        var demoDb = Path.Combine(work, "demo.db");
        File.Copy(db, demoDb);
        var (demoBackup, demoPhrase) = SealedBackup.Create(demoDb, ks, "demo-snapshot");
        var demoBackupPath = Path.Combine(work, "demo.thbackup");
        SealedBackup.Write(demoBackup, demoBackupPath);
        var demoBefore = CanonicalExport.Build(demoDb);
        // Mutate the live demo: a junk event, a valve flipped, an event deleted.
        using (var conn = new SqliteConnection($"Data Source={demoDb}"))
        {
            conn.Open();
            Exec(conn, "INSERT INTO Events (HouseholdId, Type, Body, CreatedAtUtc, ConfirmationState, SensitivityClass, AudienceRule, RecordPending) VALUES (1, 'HouseholdNote', 'junk event a visitor typed', '2026-10-28T12:00:00Z', 'Confirmed', 'Household', '{\"Class\":\"Household\"}', 0)");
            Exec(conn, "UPDATE ValveSettings SET Open=0 WHERE MaterialClass='HealthMention'");
            Exec(conn, "DELETE FROM Events WHERE Body LIKE '%pickup is at 5:30%'");
        }
        var mutated = CanonicalExport.Build(demoDb);
        Check("demo-state-really-mutated-before-reset", !CanonicalExport.Compare(demoBefore, mutated).Equal);
        var resetDest = Path.Combine(work, "demo-reset.db");
        Program.RestoreFromPhrase(demoBackupPath, demoPhrase, resetDest, "demo-reset");
        var demoAfter = CanonicalExport.Build(resetDest);
        var demoCmp = CanonicalExport.Compare(demoBefore, demoAfter);
        Check("demo-reset-restores-the-determined-original-state", demoCmp.Equal,
            $"tables={demoCmp.TablesCompared} rows={demoCmp.RowsCompared}" + (demoCmp.Equal ? "" : " diffs: " + string.Join(" ; ", demoCmp.Diffs.Take(5))));
        using (var conn = new SqliteConnection($"Data Source={resetDest};Mode=ReadOnly"))
        {
            conn.Open();
            long prov = ScalarLong(conn, "SELECT COUNT(*) FROM Events WHERE Details LIKE '%federated%' AND Details LIKE '%PacketId%'");
            long auditCount = ScalarLong(conn, "SELECT COUNT(*) FROM EventAudits WHERE Channel='restore'");
            Check("demo-reset-keeps-seeded-agent-traffic-with-provenance-and-audit-row", prov == 2 && auditCount == 1,
                $"federated events with packet provenance={prov} restore audit rows={auditCount}");
        }

        // Concurrent writes during a backup. The
        // writer commits an event and its audit row in ONE
        // transaction, over and over, while exports and a backup
        // are taken. An export read table by table with no single
        // snapshot can catch the pair torn (an audit row whose
        // event the export does not hold, or the reverse), and a
        // backup built from such a read fails its foreign key
        // check on restore. Every export taken mid write must be
        // internally consistent, and a backup taken mid write
        // must restore cleanly and compare equal to its contents.
        var concDb = Path.Combine(work, "concurrent.db");
        using (var conn = new SqliteConnection($"Data Source={concDb}"))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "CREATE TABLE Households (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, TimeZoneId TEXT NOT NULL, DefaultRoutePersonId INTEGER NULL, BackupRoutePersonId INTEGER NULL, HealthMentionLevelKey TEXT NULL)");
            Exec(conn, "CREATE TABLE Events (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, HouseholdId INTEGER NOT NULL REFERENCES Households(Id), SubjectPersonId INTEGER NULL, AuthorPersonId INTEGER NULL, Type TEXT NOT NULL, Body TEXT NOT NULL, Details TEXT NULL, CreatedAtUtc TEXT NOT NULL, ConfirmationState TEXT NOT NULL, SensitivityClass TEXT NOT NULL, AudienceRule TEXT NULL, RecordPending INTEGER NOT NULL, RelatedSupplyItemId INTEGER NULL)");
            Exec(conn, "CREATE TABLE EventAudits (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ObjectKind TEXT NOT NULL, EventId INTEGER NOT NULL REFERENCES Events(Id), FromState TEXT NULL, ToState TEXT NOT NULL, ActorPersonId INTEGER NULL, AtUtc TEXT NOT NULL, Channel TEXT NOT NULL)");
            Exec(conn, "CREATE TABLE Receipts (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, EventId INTEGER NOT NULL REFERENCES Events(Id), Note TEXT NOT NULL)");
            Exec(conn, "INSERT INTO Households (Id, Name, TimeZoneId) VALUES (1, 'Concurrent household', 'America/Chicago')");
        }
        var stopWriter = 0;
        long writtenPairs = 0;
        Exception? writerError = null;
        var writer = new Thread(() =>
        {
            try
            {
                using var wconn = new SqliteConnection($"Data Source={concDb};Default Timeout=30");
                wconn.Open();
                // Bounded, lightly paced writing: at most 600
                // pairs, one commit every few milliseconds. That
                // is plenty to tear an unsnapshotted export (the
                // table reads land seconds apart in pair terms at
                // this pace across 40 exports), and it leaves the
                // gaps a snapshot copy needs to complete.
                while (Volatile.Read(ref stopWriter) == 0 && Interlocked.Read(ref writtenPairs) < 600)
                {
                    using var tx = wconn.BeginTransaction();
                    Exec(wconn, "INSERT INTO Events (HouseholdId, Type, Body, CreatedAtUtc, ConfirmationState, SensitivityClass, AudienceRule, RecordPending) VALUES (1, 'HouseholdNote', 'concurrent pair', '2026-10-29 00:00:00', 'Confirmed', 'Household', '{\"Class\":\"Household\"}', 0)");
                    Exec(wconn, "INSERT INTO EventAudits (ObjectKind, EventId, ToState, AtUtc, Channel) VALUES ('Event', last_insert_rowid(), 'Confirmed', '2026-10-29 00:00:00', 'tap')");
                    Exec(wconn, "INSERT INTO Receipts (EventId, Note) VALUES ((SELECT MAX(Id) FROM Events), 'pair receipt')");
                    tx.Commit();
                    Interlocked.Increment(ref writtenPairs);
                    Thread.Sleep(3);
                }
            }
            catch (Exception ex) { writerError = ex; }
        });
        writer.Start();
        var spinUntil = DateTime.UtcNow.AddSeconds(10);
        while (Interlocked.Read(ref writtenPairs) < 5 && DateTime.UtcNow < spinUntil) Thread.Sleep(5);
        bool exportsConsistent = true;
        string concDetail = "";
        for (int i = 0; i < 40 && exportsConsistent; i++)
        {
            var exp = CanonicalExport.Build(concDb);
            var evTable = exp.Tables.First(t => t.Name == "Events");
            var auTable = exp.Tables.First(t => t.Name == "EventAudits");
            var rcTable = exp.Tables.First(t => t.Name == "Receipts");
            int evIdIdx = evTable.Columns.IndexOf("Id");
            int auEvIdx = auTable.Columns.IndexOf("EventId");
            int rcEvIdx = rcTable.Columns.IndexOf("EventId");
            var evIds = evTable.Rows.Select(r => r[evIdIdx].V ?? "").ToHashSet();
            var auIds = auTable.Rows.Select(r => r[auEvIdx].V ?? "").ToHashSet();
            var rcIds = rcTable.Rows.Select(r => r[rcEvIdx].V ?? "").ToHashSet();
            if (!evIds.SetEquals(auIds) || !evIds.SetEquals(rcIds))
            {
                exportsConsistent = false;
                concDetail = $"export {i} torn: events={evIds.Count} audit event ids={auIds.Count} receipt event ids={rcIds.Count}";
            }
        }
        var (concBackup, concPhrase) = SealedBackup.Create(concDb, ks, "record");
        var concBackupPath = Path.Combine(work, "concurrent.thbackup");
        SealedBackup.Write(concBackup, concBackupPath);
        Volatile.Write(ref stopWriter, 1);
        writer.Join();
        Check("backup-export-is-a-consistent-snapshot-under-concurrent-writes",
            exportsConsistent && writerError == null && writtenPairs > 0,
            concDetail.Length > 0 ? concDetail : $"writer pairs committed during exports={writtenPairs}" + (writerError != null ? " writer error: " + writerError.Message : ""));
        var concDest = Path.Combine(work, "concurrent-restored.db");
        bool concRestored = !Throws(() => Program.RestoreFromPhrase(concBackupPath, concPhrase, concDest, "recovery-key"));
        bool concEqual = false;
        string concCmpDetail = concRestored ? "" : "restore of a mid-write backup threw (foreign key check is the expected failure)";
        if (concRestored)
        {
            var cmpC = CanonicalExport.Compare(
                SealedBackup.OpenWithPhrase(SealedBackup.Read(concBackupPath), concPhrase),
                CanonicalExport.Build(concDest));
            concEqual = cmpC.Equal;
            concCmpDetail = $"tables={cmpC.TablesCompared} rows={cmpC.RowsCompared}" + (cmpC.Equal ? "" : " diffs: " + string.Join(" ; ", cmpC.Diffs.Take(3)));
        }
        Check("backup-taken-under-concurrent-writes-restores-and-compares-equal", concRestored && concEqual, concCmpDetail);

        // A failed final move must not destroy the only database.
        // The destination here is an existing
        // directory, which a file move can never replace: the move
        // fails on any platform, as any user. After the failure
        // the restored temporary file must still exist beside the
        // destination for recovery, not be deleted by cleanup.
        var e4Dir = Path.Combine(work, "move-failure");
        Directory.CreateDirectory(e4Dir);
        var e4Dest = Path.Combine(e4Dir, "node.db");
        Directory.CreateDirectory(e4Dest);
        var e4Threw = Throws(() => Program.RestoreFromPhrase(backupPath, phrase, e4Dest, "recovery-key"));
        var e4Kept = Directory.GetFiles(e4Dir, ".restore-*.db");
        Check("restore-move-failure-keeps-the-restored-file-for-recovery",
            e4Threw && e4Kept.Length == 1 && Directory.Exists(e4Dest),
            $"threw={e4Threw} kept temp files={e4Kept.Length} destination directory still present={Directory.Exists(e4Dest)}");

        // At rest file sealing for a stopped node.
        var restDb = Path.Combine(work, "rest.db");
        File.Copy(db, restDb);
        var restHash = Crypto.Sha256Hex(File.ReadAllBytes(restDb));
        var sealedPath = Path.Combine(work, "rest.db.thsealed");
        AtRestFileSeal.SealFile(restDb, sealedPath, ks);
        var sealedText = File.ReadAllText(sealedPath);
        File.Delete(restDb);
        Check("at-rest-sealed-db-file-holds-no-canary", !sealedText.Contains(Canary) && !sealedText.Contains("Margaret"));
        var unsealedPath = Path.Combine(work, "rest-restored.db");
        Check("at-rest-unseal-with-wrong-key-fails-and-writes-nothing",
            Throws(() => AtRestFileSeal.UnsealFile(sealedPath, unsealedPath, otherKs)) && !File.Exists(unsealedPath));
        AtRestFileSeal.UnsealFile(sealedPath, unsealedPath, ks);
        Check("at-rest-unseal-returns-identical-file-bytes", Crypto.Sha256Hex(File.ReadAllBytes(unsealedPath)) == restHash);

        Console.WriteLine($"SUMMARY {pass} passed, {fail} failed, {pass + fail} total, workdir {work}");
        return fail == 0 ? 0 : 1;
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static string ScalarText(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static void BuildFixture(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var sql = @"
CREATE TABLE Households (Id INTEGER NOT NULL CONSTRAINT PK_Households PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, TimeZoneId TEXT NOT NULL, DefaultRoutePersonId INTEGER NULL, BackupRoutePersonId INTEGER NULL, HealthMentionLevelKey TEXT NULL);
CREATE TABLE Persons (Id INTEGER NOT NULL CONSTRAINT PK_Persons PRIMARY KEY AUTOINCREMENT, HouseholdId INTEGER NOT NULL, DisplayName TEXT NOT NULL, Role TEXT NOT NULL, MinorFlag INTEGER NOT NULL);
CREATE TABLE Events (Id INTEGER NOT NULL CONSTRAINT PK_Events PRIMARY KEY AUTOINCREMENT, HouseholdId INTEGER NOT NULL, SubjectPersonId INTEGER NULL, AuthorPersonId INTEGER NULL, Type TEXT NOT NULL, Body TEXT NOT NULL, Details TEXT NULL, CreatedAtUtc TEXT NOT NULL, ConfirmationState TEXT NOT NULL, SensitivityClass TEXT NOT NULL, AudienceRule TEXT NULL, RecordPending INTEGER NOT NULL, RelatedSupplyItemId INTEGER NULL);
CREATE TABLE EventAudits (Id INTEGER NOT NULL CONSTRAINT PK_EventAudits PRIMARY KEY AUTOINCREMENT, ObjectKind TEXT NOT NULL, EventId INTEGER NOT NULL, FromState TEXT NULL, ToState TEXT NOT NULL, ActorPersonId INTEGER NULL, AtUtc TEXT NOT NULL, Channel TEXT NOT NULL);
CREATE TABLE Connections (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, ProviderClaim TEXT NOT NULL, Level TEXT NOT NULL, PublicKeyFingerprint TEXT NOT NULL, Standing TEXT NOT NULL, GrantedByPersonId INTEGER NULL);
CREATE TABLE ConsentGrants (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, MaterialClass TEXT NOT NULL, Basis TEXT NOT NULL, ExpiresUtc TEXT NULL, Revoked INTEGER NOT NULL);
CREATE TABLE ValveSettings (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, HouseholdId INTEGER NOT NULL, MaterialClass TEXT NOT NULL, Open INTEGER NOT NULL, SetByPersonId INTEGER NULL);
CREATE TABLE PacketSeen (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PacketId TEXT NOT NULL, ConnectionId TEXT NULL, SeenAtUtc TEXT NOT NULL);
CREATE TABLE PacketNonceUses (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ConnectionId TEXT NOT NULL, PacketNonce TEXT NOT NULL, PacketId TEXT NOT NULL, UsedAtUtc TEXT NOT NULL);
INSERT INTO PacketSeen (Id, PacketId, ConnectionId, SeenAtUtc) VALUES (1, 'pkt-before-snapshot', 'conn-lisa-01', '2026-10-28T08:00:00Z');
INSERT INTO PacketNonceUses (Id, ConnectionId, PacketNonce, PacketId, UsedAtUtc) VALUES (1, 'conn-lisa-01', 'nonce-before-snapshot', 'pkt-before-snapshot', '2026-10-28T08:00:00Z');
INSERT INTO Households (Id, Name, TimeZoneId, DefaultRoutePersonId, BackupRoutePersonId, HealthMentionLevelKey) VALUES (1, 'Ellis household', 'America/Chicago', 2, 3, 'now');
INSERT INTO Persons (Id, HouseholdId, DisplayName, Role, MinorFlag) VALUES (1,1,'Margaret Ellis','Family',0),(2,1,'Daniel','Family',0),(3,1,'Lisa','Family',0),(4,1,'Timmy','Family',1);
INSERT INTO Connections (Id, PersonId, ProviderClaim, Level, PublicKeyFingerprint, Standing, GrantedByPersonId) VALUES (1,3,'stand-in assistant (self-asserted)','Member','FP-LISA-0001','Good',1),(2,2,'stand-in assistant (self-asserted)','Member','FP-DANIEL-0002','Good',1);
INSERT INTO ConsentGrants (Id, PersonId, MaterialClass, Basis, ExpiresUtc, Revoked) VALUES (1,3,'HouseholdNote','Explicit',NULL,0),(2,2,'HealthMention','None',NULL,0);
INSERT INTO ValveSettings (Id, HouseholdId, MaterialClass, Open, SetByPersonId) VALUES (1,1,'HouseholdNote',1,1),(2,1,'HealthMention',1,1),(3,1,'Journal',0,1);
";
        Exec(conn, sql);
        // Events inserted with parameters so the canary text is exact.
        void Event(int id, int? author, string type, string body, string? details, string state, string sens, string? audience)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Events (Id, HouseholdId, SubjectPersonId, AuthorPersonId, Type, Body, Details, CreatedAtUtc, ConfirmationState, SensitivityClass, AudienceRule, RecordPending, RelatedSupplyItemId) VALUES (@id,1,NULL,@a,@t,@b,@d,'2026-10-28T09:00:00Z',@s,@sc,@ar,0,NULL)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@a", author is null ? DBNull.Value : author.Value);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@b", body);
            cmd.Parameters.AddWithValue("@d", details is null ? DBNull.Value : details);
            cmd.Parameters.AddWithValue("@s", state);
            cmd.Parameters.AddWithValue("@sc", sens);
            cmd.Parameters.AddWithValue("@ar", audience is null ? DBNull.Value : audience);
            cmd.ExecuteNonQuery();
        }
        Event(1, 2, "HouseholdNote", Canary, null, "Confirmed", "Household", "{\"Class\":\"Household\"}");
        Event(2, 1, "HealthMention", "My ankle has swollen up again.", null, "Confirmed", "Restricted", "{\"Class\":\"Restricted\",\"RoutePersonId\":2,\"BackupPersonId\":3}");
        Event(3, 3, "Journal", "A private journal line.", null, "Confirmed", "Personal", "{\"Class\":\"Personal\"}");
        Event(4, 3, "HouseholdNote", "Timmy's pickup is at 5:30, we need milk", "{\"Origin\":\"federated\",\"Connection\":\"lisa-assistant\",\"PacketId\":\"pkt-lisa-0001\"}", "Confirmed", "Household", "{\"Class\":\"Household\"}");
        Event(5, 2, "HouseholdNote", "Daniel confirmed the plan by his assistant.", "{\"Origin\":\"federated\",\"Connection\":\"daniel-assistant\",\"PacketId\":\"pkt-daniel-0002\"}", "Confirmed", "Household", "{\"Class\":\"Household\"}");
        Exec(conn, "INSERT INTO EventAudits (Id, ObjectKind, EventId, FromState, ToState, ActorPersonId, AtUtc, Channel) VALUES (1,'Event',1,NULL,'Confirmed',2,'2026-10-28T09:00:01Z','tap'),(2,'Event',4,NULL,'Confirmed',3,'2026-10-28T09:01:00Z','federated')");
    }
}
