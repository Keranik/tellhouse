using Microsoft.Data.Sqlite;

namespace TellhouseBackup;

// Proof harness for the backup tool. Builds a fixture node database in the node
// record shape (Households, Persons, Events, EventAudits) plus the
// membership, grant, Consent and valve tables the split node adds
// (Connections, ConsentGrants, ValveSettings), then runs the restore
// proof: field for field comparison, stored
// audience equality, wrong key failure with no partial record, canary
// absence from the sealed bytes, demo reset through the same machinery,
// and at rest file sealing for a stopped node.
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
            Exec(conn, "INSERT INTO Events (HouseholdId, Type, Body, CreatedAtUtc, ConfirmationState, SensitivityClass, AudienceRule, RecordPending) VALUES (1, 'HouseholdNote', 'junk event a judge typed', '2026-10-28T12:00:00Z', 'Confirmed', 'Household', '{\"Class\":\"Household\"}', 0)");
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
