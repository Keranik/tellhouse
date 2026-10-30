using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TellhouseBackup;

// Sealed backup format. A fresh random data key seals the canonical
// export. That data key is wrapped twice: once under the household
// content key (the node path) and once under the recovery secret (the
// household path). The recovery secret itself is never stored anywhere:
// it is shown once as a phrase at backup time and the node keeps only
// the wrapped copy inside the backup header. A backup file on its own,
// or a backup plus everything on a relay, opens with neither wrap and
// restores nothing.
public sealed class BackupFile
{
    public string Format { get; set; } = "tellhouse-sealed-backup";
    public int Version { get; set; } = 1;
    public string BackupId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string Label { get; set; } = ""; // "record" or "demo-snapshot"
    public string ContentKeyId { get; set; } = "";
    public Crypto.SealedBlob ContentKeyWrap { get; set; } = new("", "", "");
    public Crypto.SealedBlob RecoveryWrap { get; set; } = new("", "", "");
    public string PlaintextSha256 { get; set; } = "";
    public Crypto.SealedBlob Payload { get; set; } = new("", "", "");
}

public static class SealedBackup
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // Creates a sealed backup of dbPath. Returns the backup and the
    // recovery phrase. The phrase exists in memory only at this moment;
    // the caller shows it once and the tool never writes it to disk.
    public static (BackupFile Backup, string RecoveryPhrase) Create(string dbPath, ContentKeyStore keyStore, string label)
    {
        // Flush the write-ahead log into the main file first,
        // so the export is taken from the checkpointed
        // database. If a live writer holds the checkpoint busy,
        // the export still proceeds: a SQLite read sees the WAL
        // logically even when it cannot be truncated yet.
        try { StoppedDb.FlushWal(dbPath); }
        catch (InvalidOperationException) { }
        var export = CanonicalExport.Build(dbPath);
        var plain = Crypto.Utf8(CanonicalExport.Serialize(export));
        var dataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var recoverySecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var backup = new BackupFile
        {
            BackupId = "backup-" + Guid.NewGuid().ToString("N")[..12],
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            Label = label,
            ContentKeyId = keyStore.KeyId,
            ContentKeyWrap = Crypto.Seal(keyStore.Key, dataKey),
            RecoveryWrap = Crypto.Seal(recoverySecret, dataKey),
            PlaintextSha256 = Crypto.Sha256Hex(plain),
            Payload = Crypto.Seal(dataKey, plain),
        };
        var phrase = WordList.Encode(recoverySecret);
        // The secrets do not leave this frame in any stored form.
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(recoverySecret);
        return (backup, phrase);
    }

    public static void Write(BackupFile backup, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(backup, Opts));

    public static BackupFile Read(string path) =>
        JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path))
        ?? throw new InvalidOperationException("backup file is unreadable");

    public static CanonicalExport.Export OpenWithPhrase(BackupFile backup, string phrase)
    {
        var secret = WordList.Decode(phrase);
        var dataKey = Crypto.Open(secret, backup.RecoveryWrap);
        return OpenWithDataKey(backup, dataKey);
    }

    public static CanonicalExport.Export OpenWithContentKey(BackupFile backup, ContentKeyStore keyStore)
    {
        if (backup.ContentKeyId != keyStore.KeyId)
            throw new InvalidOperationException("backup was sealed under a different household content key");
        var dataKey = Crypto.Open(keyStore.Key, backup.ContentKeyWrap);
        return OpenWithDataKey(backup, dataKey);
    }

    private static CanonicalExport.Export OpenWithDataKey(BackupFile backup, byte[] dataKey)
    {
        var plain = Crypto.Open(dataKey, backup.Payload);
        if (Crypto.Sha256Hex(plain) != backup.PlaintextSha256)
            throw new InvalidOperationException("backup plaintext hash does not match its header");
        return CanonicalExport.Parse(Crypto.FromUtf8(plain));
    }
}

// Guards for operations that treat a database file as a stopped
// node's file. Two distinct hazards live here:
//
//   - Rows can sit in the write-ahead log beside the main file.
//     Anything that reads, seals, or replaces the main file alone
//     silently loses or resurrects them. FlushWal checkpoints the
//     WAL into the main file and truncates it, and refuses when
//     the checkpoint stays busy, which is what a live writer
//     holding a transaction looks like.
//   - A second process can hold the database open. Swapping the
//     file under it reports success while that process keeps
//     working on the file it opened: its record does not change
//     and its later writes land in an unlinked file and vanish.
//     AssertNotHeldByAnotherProcess refuses that case when it is
//     observable: on Linux, by scanning /proc for another process
//     holding the database or one of its journal sidecars open.
//     Where the holder is not observable from here (a node in a
//     different container, for one), the stop-first rule in
//     DEPLOY.md remains in force; this guard is
//     the enforcement for the shapes where the holder is visible.
public static class StoppedDb
{
    public static void FlushWal(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return;
        }
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=2000";
        cmd.ExecuteNonQuery();
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = check.ExecuteReader();
        if (reader.Read() && reader.GetInt32(0) != 0)
        {
            throw new InvalidOperationException(
                "the database at " + dbPath + " could not be checkpointed because it is in use; stop the node first");
        }
    }

    public static void AssertNotHeldByAnotherProcess(string dbPath)
    {
        if (!File.Exists(dbPath) || !OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return;
        }
        var full = Path.GetFullPath(dbPath);
        var targets = new HashSet<string> { full, full + "-wal", full + "-shm", full + "-journal" };
        var self = Environment.ProcessId.ToString();
        foreach (var procDir in Directory.GetDirectories("/proc"))
        {
            var pid = Path.GetFileName(procDir);
            if (pid == self || !pid.All(char.IsDigit))
            {
                continue;
            }
            string fdDir = Path.Combine(procDir, "fd");
            string[] fds;
            try { fds = Directory.GetFiles(fdDir); }
            catch { continue; }
            foreach (var fd in fds)
            {
                string? target = null;
                try { target = File.ResolveLinkTarget(fd, returnFinalTarget: true)?.FullName; }
                catch { }
                if (target is null)
                {
                    continue;
                }
                var bare = target.EndsWith(" (deleted)", StringComparison.Ordinal)
                    ? target[..^" (deleted)".Length]
                    : target;
                if (targets.Contains(bare))
                {
                    throw new InvalidOperationException(
                        "the database at " + dbPath + " is held open by another process (pid " + pid + "); stop the node before restoring or sealing over it");
                }
            }
        }
    }

    // Both guards, in the order a file operation needs them: the
    // live holder refusal first, then the checkpoint.
    public static void Prepare(string dbPath)
    {
        AssertNotHeldByAnotherProcess(dbPath);
        FlushWal(dbPath);
    }
}

// Restore onto a fresh node database file, plus the at rest file sealing
// for a stopped node's database (see README for the honest scope of the
// at rest part).
public static class Restore
{
    // Builds the destination database from the export, writes the restore
    // announcement event and its audit row, and only then moves the file
    // into place. A wrong key fails earlier, at open time, and leaves no
    // destination file. A failure while building leaves the destination
    // untouched and keeps the partially built file beside it for
    // diagnosis; a failure of the final move itself keeps the complete
    // restored file at the same place, so a failed restore never
    // leaves a household with no database at all.
    // The seen packet tables a restore must never roll back: the
    // packet_id registry and the per connection nonce registry.
    // Their key columns decide what "the same row" means when the
    // destination's rows are merged into the restored database.
    private static readonly Dictionary<string, string[]> SeenTableKeys = new()
    {
        ["PacketSeen"] = new[] { "PacketId" },
        ["PacketNonceUses"] = new[] { "ConnectionId", "PacketNonce" },
    };

    public static void RestoreToFreshNode(CanonicalExport.Export export, BackupFile backup, string destDbPath, string pathLabel, bool overrideLiveNode = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destDbPath))!;
        Directory.CreateDirectory(dir);
        // This is a file swap, so the destination must be a
        // stopped node's file. Refuse a destination another
        // process holds open, and checkpoint its WAL first: the
        // checkpoint both flushes rows that only the WAL holds
        // (which the seen table capture below must be able to
        // read) and proves no live writer is mid transaction.
        var preservedSeen = new List<CanonicalExport.TableExport>();
        if (File.Exists(destDbPath))
        {
            // The liveness lock is checked ahead of the holder
            // probe below. The probe only sees processes in this
            // container's namespace; the lock is the guard that also
            // covers a node running in another container that shares
            // this database directory. A fresh lock refuses the swap
            // (or forces the logged override) before anything reads
            // or checkpoints the live file.
            NodeLiveness.AssertResetAllowed(destDbPath, overrideLiveNode,
                "restore (" + pathLabel + ")");
            StoppedDb.Prepare(destDbPath);
            preservedSeen = CaptureSeenTables(destDbPath);
            // The capture read through a pooled connection, and a
            // pooled connection keeps the destination's old file
            // open across the swap below: any later read through
            // this process would then see the old record, not the
            // restored one. That is the running node hazard in
            // miniature, so the pools are cleared before the swap.
            SqliteConnection.ClearAllPools();
        }
        var tmpPath = Path.Combine(dir, ".restore-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        {
            using (var conn = new SqliteConnection($"Data Source={tmpPath}"))
            {
                conn.Open();
                Exec(conn, "PRAGMA foreign_keys=OFF");
                // Tables first, in recorded order, then data, then the rest
                // of the schema (indexes, triggers, views).
                foreach (var s in export.Schema.Where(s => s.Type == "table"))
                    Exec(conn, s.Sql!);
                foreach (var table in export.Tables)
                    InsertTable(conn, export, table);
                foreach (var s in export.Schema.Where(s => s.Type != "table"))
                    Exec(conn, s.Sql!);
                // Merge the destination's seen packet rows into
                // the restored database. A packet accepted after
                // the snapshot was taken has its
                // only replay protection in these rows; a restore
                // that rolls them back lets a re-posted copy of
                // that packet be recorded a second time once the
                // relay's short in-memory duplicate window passes.
                MergeSeenTables(conn, preservedSeen);
                var fk = Scalar(conn, "PRAGMA foreign_key_check");
                if (fk != null) throw new InvalidOperationException("foreign key check failed after restore");
                WriteAnnouncement(conn, export, backup, pathLabel);
            }
            // The destination must be a stopped node's file (see the
            // README and DEPLOY.md: never restore onto a database a
            // running node has open). A stopped or crashed destination
            // can still leave journal sidecar files behind, and those
            // files belong to the old database file, not to the one
            // being moved into place. If they survived the swap,
            // SQLite would replay or consult them against the restored
            // file on the next open, which can resurrect pre restore
            // pages into the restored record (observed in testing:
            // one table came back holding another table's rows, with
            // integrity_check failing). Remove them before the swap.
            DeleteSidecars(destDbPath);
            // One atomic replace, never delete then move:
            // deleting the live database first left a window
            // where a failed move destroyed the only copy, and the
            // old cleanup then deleted the restored file too. With
            // overwrite move semantics the destination is replaced
            // in a single step; if the move throws, the restored
            // file is still at tmpPath, and it is deliberately NOT
            // cleaned up here, so recovery can put it in place by
            // hand. On success tmpPath no longer exists (it IS the
            // destination), so success needs no cleanup either.
            File.Move(tmpPath, destDbPath, overwrite: true);
        }
    }

    // Journal sidecar files for a database path: the WAL and its
    // shared memory file, plus the older rollback journal. They are
    // state of the database file they sit next to, never of a file
    // swapped in later, so a file swap must remove them first.
    public static void DeleteSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
    }

    // Reads the destination's current seen packet tables, after
    // its WAL has been checkpointed, so rows a live node wrote
    // into the WAL are read as well. A destination that cannot be
    // read as a database at all carries no readable seen rows;
    // the restore replaces it wholesale, which is the recovery
    // path for a corrupt destination file.
    private static List<CanonicalExport.TableExport> CaptureSeenTables(string destDbPath)
    {
        var tables = new List<CanonicalExport.TableExport>();
        try
        {
            var current = CanonicalExport.Build(destDbPath);
            foreach (var name in SeenTableKeys.Keys)
            {
                var table = current.Tables.FirstOrDefault(t => t.Name == name);
                if (table is not null)
                {
                    tables.Add(table);
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
        }
        return tables;
    }

    // Inserts each preserved seen row that the restored database
    // does not already carry, keyed by the table's key columns.
    // The Id column is never carried: a preserved row takes a
    // fresh id in the restored table, so it can never collide
    // with a restored row's id.
    private static void MergeSeenTables(SqliteConnection conn, List<CanonicalExport.TableExport> preserved)
    {
        foreach (var table in preserved)
        {
            var keys = SeenTableKeys[table.Name];
            if (Scalar(conn, $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table.Name}'") is not string)
            {
                continue;
            }
            var insertCols = table.Columns.Where(c => c != "Id").ToList();
            foreach (var row in table.Rows)
            {
                var where = string.Join(" AND ", keys.Select(k => $"\"{k}\"=@k{table.Columns.IndexOf(k)}"));
                using (var check = conn.CreateCommand())
                {
                    check.CommandText = $"SELECT COUNT(*) FROM \"{table.Name}\" WHERE {where}";
                    foreach (var k in keys)
                    {
                        var idx = table.Columns.IndexOf(k);
                        var p = check.CreateParameter();
                        p.ParameterName = "@k" + idx;
                        p.Value = row[idx].V ?? (object)DBNull.Value;
                        check.Parameters.Add(p);
                    }
                    if ((long)check.ExecuteScalar()! > 0)
                    {
                        continue;
                    }
                }
                var cols = string.Join(",", insertCols.Select(c => $"\"{c}\""));
                var pars = string.Join(",", insertCols.Select(c => "@p" + table.Columns.IndexOf(c)));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO \"{table.Name}\" ({cols}) VALUES ({pars})";
                foreach (var c in insertCols)
                {
                    var cell = row[table.Columns.IndexOf(c)];
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@p" + table.Columns.IndexOf(c);
                    p.Value = cell.T switch
                    {
                        "n" => DBNull.Value,
                        "i" => long.Parse(cell.V!),
                        "r" => double.Parse(cell.V!, System.Globalization.CultureInfo.InvariantCulture),
                        "b" => Convert.FromBase64String(cell.V!),
                        _ => cell.V ?? "",
                    };
                    cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void InsertTable(SqliteConnection conn, CanonicalExport.Export export, CanonicalExport.TableExport table)
    {
        // sqlite_sequence is created by SQLite itself when the first
        // AUTOINCREMENT table lands; only its rows are restored.
        if (table.Name == "sqlite_sequence")
        {
            using var check = conn.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sqlite_sequence'";
            if (check.ExecuteScalar() is not string) return;
        }
        var cols = string.Join(",", table.Columns.Select(c => $"\"{c}\""));
        var pars = string.Join(",", table.Columns.Select((_, i) => "@p" + i));
        int seqNameIdx = table.Name == "sqlite_sequence" ? Math.Max(0, table.Columns.IndexOf("name")) : -1;
        foreach (var row in table.Rows)
        {
            if (seqNameIdx >= 0)
            {
                // Replace, never add. Inserting the data rows has
                // already created one sqlite_sequence row per
                // AUTOINCREMENT table, holding the largest id actually
                // inserted. A plain extra INSERT here duplicated that
                // row, and SQLite reads the first row for a table, so
                // the exported high water mark was ignored whenever it
                // sat above the largest surviving id (rows deleted at
                // the tail before the backup). Deleted ids would then
                // be handed out again after a restore. Deleting the
                // auto created row first leaves exactly one row per
                // table, carrying the exported value.
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM sqlite_sequence WHERE name=@n";
                del.Parameters.AddWithValue("@n", row[seqNameIdx].V ?? "");
                del.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO \"{table.Name}\" ({cols}) VALUES ({pars})";
            for (int i = 0; i < row.Count; i++)
            {
                var cell = row[i];
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                p.Value = cell.T switch
                {
                    "n" => DBNull.Value,
                    "i" => long.Parse(cell.V!),
                    "r" => double.Parse(cell.V!, System.Globalization.CultureInfo.InvariantCulture),
                    "b" => Convert.FromBase64String(cell.V!),
                    _ => cell.V ?? "",
                };
                cmd.Parameters.Add(p);
            }
            cmd.ExecuteNonQuery();
        }
    }

    // The restore is announced in the record: one HouseholdNote event
    // carrying the RestoreAnnouncement marker in its Details, and one
    // EventAudits row for it with channel "restore". Actor is null: the
    // restore is a household key ceremony, not a person's telling, and
    // the audit model already allows a null actor for executor rows.
    private static void WriteAnnouncement(SqliteConnection conn, CanonicalExport.Export export, BackupFile backup, string pathLabel)
    {
        var events = export.Tables.FirstOrDefault(t => t.Name == "Events");
        var audits = export.Tables.FirstOrDefault(t => t.Name == "EventAudits");
        var households = export.Tables.FirstOrDefault(t => t.Name == "Households");
        if (events == null || audits == null || households == null || households.Rows.Count == 0) return;
        long householdId = long.Parse(households.Rows[0][households.Columns.IndexOf("Id")].V!);
        // The app's own timestamp text form: the node
        // stores DateTime values as "yyyy-MM-dd HH:mm:ss.FFFFFFF"
        // (space separator, no zone suffix), and the timeline
        // orders events by that stored text. A round trip ("o")
        // string here sorted the announcement above every same
        // day row regardless of its clock time.
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF",
            System.Globalization.CultureInfo.InvariantCulture);
        long eventId;
        using (var cmd = conn.CreateCommand())
        {
            // The node's Events table carries two NOT NULL
            // columns this tool's own fixture predates
            // (ReportedNotObserved, AudienceAskPending, both from
            // the node's entities). The announcement is neither
            // reported-not-observed nor ask-pending, so when those
            // columns exist they are written as 0; a record whose
            // table predates them takes the original insert.
            var extraCols = "";
            var extraVals = "";
            if (events.Columns.Contains("ReportedNotObserved"))
            {
                extraCols += ", ReportedNotObserved";
                extraVals += ", 0";
            }
            if (events.Columns.Contains("AudienceAskPending"))
            {
                extraCols += ", AudienceAskPending";
                extraVals += ", 0";
            }
            cmd.CommandText = "INSERT INTO Events (HouseholdId, SubjectPersonId, AuthorPersonId, Type, Body, Details, CreatedAtUtc, ConfirmationState, SensitivityClass, AudienceRule, RecordPending, RelatedSupplyItemId" + extraCols + ") VALUES (@h, NULL, NULL, 'HouseholdNote', @body, @details, @now, 'Confirmed', 'Household', @aud, 0, NULL" + extraVals + "); SELECT last_insert_rowid()";
            var body = "Household record restored from a sealed backup. Restore path: " + pathLabel + ". Backup created at " + backup.CreatedUtc + ".";
            var details = "{\"RestoreAnnouncement\":true,\"BackupId\":\"" + backup.BackupId + "\",\"Path\":\"" + pathLabel + "\"}";
            cmd.Parameters.AddWithValue("@h", householdId);
            cmd.Parameters.AddWithValue("@body", body);
            cmd.Parameters.AddWithValue("@details", details);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@aud", "{\"Class\":\"Household\"}");
            eventId = (long)cmd.ExecuteScalar()!;
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO EventAudits (ObjectKind, EventId, FromState, ToState, ActorPersonId, AtUtc, Channel) VALUES ('Event', @e, NULL, 'Confirmed', NULL, @now, 'restore')";
            cmd.Parameters.AddWithValue("@e", eventId);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == DBNull.Value ? null : v;
    }
}

// Whole file sealing for a stopped node's database. Scope, stated
// plainly: this makes the database file ciphertext while the node is
// stopped. It is not transparent live database encryption; see the
// divergence note in the README.
public static class AtRestFileSeal
{
    public sealed class SealedFile
    {
        public string Format { get; set; } = "tellhouse-sealed-db-file";
        public int Version { get; set; } = 1;
        public string ContentKeyId { get; set; } = "";
        public string PlaintextSha256 { get; set; } = "";
        public Crypto.SealedBlob Payload { get; set; } = new("", "", "");
    }

    public static void SealFile(string dbPath, string sealedPath, ContentKeyStore keyStore, bool overrideLiveNode = false)
    {
        // Reset protection: sealing reads the node's database
        // as a stopped file, so it takes the same live-node
        // refusal as a restore.
        NodeLiveness.AssertResetAllowed(dbPath, overrideLiveNode, "at-rest seal");
        // Seal the checkpointed database. Reading the main
        // file alone sealed an empty shell whenever the
        // node's recent rows were still in its write-ahead log,
        // which on a fresh WAL mode database was all of them.
        StoppedDb.Prepare(dbPath);
        var bytes = File.ReadAllBytes(dbPath);
        var sealedFile = new SealedFile
        {
            ContentKeyId = keyStore.KeyId,
            PlaintextSha256 = Crypto.Sha256Hex(bytes),
            Payload = Crypto.Seal(keyStore.Key, bytes),
        };
        // The hash sits inside the sealed payload check on unseal as well:
        // it is stored here in the clear only as a file label, and unseal
        // verifies the opened bytes against it before writing anything.
        sealedFile.PlaintextSha256 = Crypto.Sha256Hex(bytes);
        File.WriteAllText(sealedPath, JsonSerializer.Serialize(sealedFile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void UnsealFile(string sealedPath, string dbPath, ContentKeyStore keyStore, bool overrideLiveNode = false)
    {
        // Unsealing swaps the database file, so it takes the
        // same live-node refusal as a restore, ahead of any work.
        NodeLiveness.AssertResetAllowed(dbPath, overrideLiveNode, "at-rest unseal");
        var sealedFile = JsonSerializer.Deserialize<SealedFile>(File.ReadAllText(sealedPath))
                         ?? throw new InvalidOperationException("sealed file is unreadable");
        if (sealedFile.ContentKeyId != keyStore.KeyId) throw new InvalidOperationException("sealed file belongs to a different household content key");
        var bytes = Crypto.Open(keyStore.Key, sealedFile.Payload);
        if (Crypto.Sha256Hex(bytes) != sealedFile.PlaintextSha256) throw new InvalidOperationException("unsealed bytes do not match the sealed file hash");
        var tmp = dbPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        // Same swap rule as restore: this is a stopped node's file,
        // it must not be held open by another process, and any
        // journal sidecars at the destination belong to the
        // old file, so they are removed before the swap. The swap
        // itself is one overwrite move: the old file is never
        // deleted ahead of its replacement, and if the move
        // fails the unsealed file stays at tmp for recovery.
        if (File.Exists(dbPath))
        {
            StoppedDb.AssertNotHeldByAnotherProcess(dbPath);
        }
        Restore.DeleteSidecars(dbPath);
        File.Move(tmp, dbPath, overwrite: true);
    }
}
