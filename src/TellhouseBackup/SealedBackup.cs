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

// Restore onto a fresh node database file, plus the at rest file sealing
// for a stopped node's database (see README for the honest scope of the
// at rest part).
public static class Restore
{
    // Builds the destination database from the export, writes the restore
    // announcement event and its audit row, and only then moves the file
    // into place. Any failure before the move leaves no destination file:
    // a wrong key restore fails completely, with no partial record.
    public static void RestoreToFreshNode(CanonicalExport.Export export, BackupFile backup, string destDbPath, string pathLabel)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destDbPath))!;
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, ".restore-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
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
                var fk = Scalar(conn, "PRAGMA foreign_key_check");
                if (fk != null) throw new InvalidOperationException("foreign key check failed after restore");
                WriteAnnouncement(conn, export, backup, pathLabel);
            }
            if (File.Exists(destDbPath)) File.Delete(destDbPath);
            File.Move(tmpPath, destDbPath);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
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
        foreach (var row in table.Rows)
        {
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
        var now = DateTime.UtcNow.ToString("o");
        long eventId;
        using (var cmd = conn.CreateCommand())
        {
            // Integration fix: the node's Events table carries two
            // NOT NULL columns the backup tool's own fixture predates
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
// divergence note in this project's README.
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

    public static void SealFile(string dbPath, string sealedPath, ContentKeyStore keyStore)
    {
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

    public static void UnsealFile(string sealedPath, string dbPath, ContentKeyStore keyStore)
    {
        var sealedFile = JsonSerializer.Deserialize<SealedFile>(File.ReadAllText(sealedPath))
                         ?? throw new InvalidOperationException("sealed file is unreadable");
        if (sealedFile.ContentKeyId != keyStore.KeyId) throw new InvalidOperationException("sealed file belongs to a different household content key");
        var bytes = Crypto.Open(keyStore.Key, sealedFile.Payload);
        if (Crypto.Sha256Hex(bytes) != sealedFile.PlaintextSha256) throw new InvalidOperationException("unsealed bytes do not match the sealed file hash");
        var tmp = dbPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(dbPath)) File.Delete(dbPath);
        File.Move(tmp, dbPath);
    }
}
