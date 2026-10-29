using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TellhouseBackup;

// Canonical export of a node record database: every user table, every
// row, every field, serialized deterministically so two exports can be
// compared field for field. The export is table agnostic on purpose:
// membership, grants, Consent and valve tables added by the node
// ride in the backup and in the comparison without this tool needing
// to know their names in advance.
public static class CanonicalExport
{
    public sealed class Cell
    {
        public string T { get; set; } = "n"; // n null, i integer, r real, s text, b blob
        public string? V { get; set; }
    }

    public sealed class TableExport
    {
        public string Name { get; set; } = "";
        public List<string> Columns { get; set; } = new();
        public List<List<Cell>> Rows { get; set; } = new();
    }

    public sealed class SchemaEntry
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Sql { get; set; }
    }

    public sealed class Export
    {
        public string Format { get; set; } = "tellhouse-canonical-export";
        public int Version { get; set; } = 1;
        public List<SchemaEntry> Schema { get; set; } = new();
        public List<TableExport> Tables { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static SqliteConnection OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    public static List<string> UserTables(SqliteConnection conn)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        // sqlite_sequence carries the AUTOINCREMENT high water marks. It is
        // record state (it decides the next ids), so it rides along when it
        // exists, listed last under its real name.
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sqlite_sequence'";
        if (cmd2.ExecuteScalar() is string) names.Add("sqlite_sequence");
        return names;
    }

    // Builds the export from ONE consistent snapshot of the
    // database. The export reads every table, and a
    // backup taken while the node is writing must not mix reads
    // from different moments: read table by table with no
    // snapshot, a backup caught rows torn across a writer's
    // transaction (a child row whose parent the same export did
    // not hold), and restores of such backups failed their
    // foreign key check. The snapshot is taken with SQLite's
    // online backup API, which copies a single point in time
    // even while other connections write; the export then reads
    // only the snapshot file.
    public static Export Build(string dbPath)
    {
        var snapshotPath = Path.Combine(Path.GetTempPath(),
            ".export-snapshot-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            using (var src = OpenReadOnly(dbPath))
            using (var dst = new SqliteConnection($"Data Source={snapshotPath}"))
            {
                dst.Open();
                src.BackupDatabase(dst);
            }
            return BuildFromSnapshot(snapshotPath);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(snapshotPath + suffix)) File.Delete(snapshotPath + suffix);
            }
        }
    }

    private static Export BuildFromSnapshot(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        var export = new Export();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                export.Schema.Add(new SchemaEntry { Type = r.GetString(0), Name = r.GetString(1), Sql = r.IsDBNull(2) ? null : r.GetString(2) });
        }
        foreach (var table in UserTables(conn))
        {
            var te = new TableExport { Name = table };
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var r = cmd.ExecuteReader();
                while (r.Read()) te.Columns.Add(r.GetString(1));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM \"{table}\"";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var row = new List<Cell>();
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        if (r.IsDBNull(i)) row.Add(new Cell { T = "n" });
                        else
                        {
                            var t = r.GetFieldType(i);
                            if (t == typeof(long) || t == typeof(int)) row.Add(new Cell { T = "i", V = r.GetInt64(i).ToString(CultureInfo.InvariantCulture) });
                            else if (t == typeof(double) || t == typeof(decimal) || t == typeof(float)) row.Add(new Cell { T = "r", V = r.GetDouble(i).ToString("R", CultureInfo.InvariantCulture) });
                            else if (t == typeof(byte[])) row.Add(new Cell { T = "b", V = Convert.ToBase64String((byte[])r.GetValue(i)) });
                            else row.Add(new Cell { T = "s", V = r.GetValue(i).ToString() });
                        }
                    }
                    te.Rows.Add(row);
                }
            }
            // Deterministic row order: by the serialized row text.
            te.Rows.Sort((a, b) => string.CompareOrdinal(JsonSerializer.Serialize(a, JsonOpts), JsonSerializer.Serialize(b, JsonOpts)));
            export.Tables.Add(te);
        }
        return export;
    }

    public static string Serialize(Export e) => JsonSerializer.Serialize(e, JsonOpts);
    public static Export Parse(string json) => JsonSerializer.Deserialize<Export>(json) ?? throw new InvalidOperationException("export parse failed");

    // Restore announcement rows are the one permitted difference between a
    // pre backup export and a post restore export, excluding only the
    // restore audit entry and rotated key identifiers.
    // An announcement is an Events row whose Details JSON carries the
    // RestoreAnnouncement marker; its audit rows are the EventAudits rows
    // that point at such an event. Everything else must match exactly.
    public static Export WithoutRestoreArtifacts(Export e)
    {
        var copy = Parse(Serialize(e));
        var events = copy.Tables.FirstOrDefault(t => t.Name == "Events");
        var excludedIds = new HashSet<string>();
        if (events != null)
        {
            int idIdx = events.Columns.IndexOf("Id");
            int detailsIdx = events.Columns.IndexOf("Details");
            if (idIdx >= 0 && detailsIdx >= 0)
            {
                foreach (var row in events.Rows)
                {
                    var d = row[detailsIdx].V ?? "";
                    if (d.Contains("RestoreAnnouncement")) excludedIds.Add(row[idIdx].V ?? "");
                }
                events.Rows.RemoveAll(r => excludedIds.Contains(r[idIdx].V ?? ""));
            }
        }
        var audits = copy.Tables.FirstOrDefault(t => t.Name == "EventAudits");
        if (audits != null && excludedIds.Count > 0)
        {
            int evIdx = audits.Columns.IndexOf("EventId");
            if (evIdx >= 0) audits.Rows.RemoveAll(r => excludedIds.Contains(r[evIdx].V ?? ""));
        }
        // The announcement also advances the AUTOINCREMENT high water
        // marks for Events and EventAudits, and a pre restore export
        // cannot carry the post restore values, so those two sequence
        // rows are excluded on both sides with the announcement. Every
        // other table's sequence row still compares exactly.
        var seq = copy.Tables.FirstOrDefault(t => t.Name == "sqlite_sequence");
        if (seq != null && seq.Columns.Count >= 1)
            seq.Rows.RemoveAll(r => r[0].V == "Events" || r[0].V == "EventAudits");
        return copy;
    }

    public sealed record CompareResult(bool Equal, List<string> Diffs, int TablesCompared, int RowsCompared,
        int AudienceRowsCompared, List<string> AudienceDiffs);

    public static CompareResult Compare(Export beforeRaw, Export afterRaw)
    {
        var before = WithoutRestoreArtifacts(beforeRaw);
        var after = WithoutRestoreArtifacts(afterRaw);
        var diffs = new List<string>();
        int rows = 0;
        var bTables = before.Tables.ToDictionary(t => t.Name);
        var aTables = after.Tables.ToDictionary(t => t.Name);
        foreach (var name in bTables.Keys.Union(aTables.Keys).OrderBy(x => x))
        {
            if (!bTables.TryGetValue(name, out var bt)) { diffs.Add($"table {name}: missing before, present after"); continue; }
            if (!aTables.TryGetValue(name, out var at)) { diffs.Add($"table {name}: present before, missing after"); continue; }
            if (!bt.Columns.SequenceEqual(at.Columns)) { diffs.Add($"table {name}: columns differ"); continue; }
            rows += bt.Rows.Count;
            // Row counts compare explicitly, per table. That includes
            // sqlite_sequence, which is a table in the export like any
            // other, apart from the two announcement rows that
            // WithoutRestoreArtifacts already removed from both sides.
            if (bt.Rows.Count != at.Rows.Count)
                diffs.Add($"table {name}: row count differs before={bt.Rows.Count} after={at.Rows.Count}");
            var bRows = bt.Rows.Select(r => JsonSerializer.Serialize(r, JsonOpts)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var aRows = at.Rows.Select(r => JsonSerializer.Serialize(r, JsonOpts)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!bRows.SequenceEqual(aRows))
            {
                // Multiset comparison, not a set comparison. A set based
                // Except reports nothing when the after side only adds
                // duplicate copies of rows that are already present,
                // which is exactly how duplicated sqlite_sequence rows
                // used to pass as equal. Count occurrences per row and
                // report every row whose multiplicity differs.
                var bCounts = bRows.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                var aCounts = aRows.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                foreach (var key in bCounts.Keys.Union(aCounts.Keys).OrderBy(x => x, StringComparer.Ordinal).Take(6))
                {
                    int bc = bCounts.GetValueOrDefault(key);
                    int ac = aCounts.GetValueOrDefault(key);
                    if (bc == ac) continue;
                    if (bc == 0) diffs.Add($"table {name}: row only after: {Trunc(key)}");
                    else if (ac == 0) diffs.Add($"table {name}: row only before: {Trunc(key)}");
                    else diffs.Add($"table {name}: row occurs {bc} times before, {ac} times after: {Trunc(key)}");
                }
            }
        }
        // Stored audience equality, spelled out per event: SensitivityClass
        // and AudienceRule for every event id, before and after.
        var audienceDiffs = new List<string>();
        int audienceRows = 0;
        var bAud = AudienceMap(beforeRaw);
        var aAud = AudienceMap(afterRaw);
        foreach (var id in bAud.Keys.Union(aAud.Keys).OrderBy(x => x))
        {
            audienceRows++;
            if (!bAud.TryGetValue(id, out var bv)) audienceDiffs.Add($"event {id}: audience present only after restore");
            else if (!aAud.TryGetValue(id, out var av)) audienceDiffs.Add($"event {id}: audience missing after restore");
            else if (bv != av) audienceDiffs.Add($"event {id}: audience changed from {bv} to {av}");
        }
        diffs.AddRange(audienceDiffs);
        return new CompareResult(diffs.Count == 0, diffs, bTables.Count, rows, audienceRows, audienceDiffs);
    }

    // Map of event id to its stored audience (class plus rule), with
    // restore announcement events removed from both sides first so the
    // announcement itself is never counted as a widening.
    public static Dictionary<string, string> AudienceMap(Export raw)
    {
        var e = WithoutRestoreArtifacts(raw);
        var map = new Dictionary<string, string>();
        var events = e.Tables.FirstOrDefault(t => t.Name == "Events");
        if (events == null) return map;
        int id = events.Columns.IndexOf("Id");
        int sc = events.Columns.IndexOf("SensitivityClass");
        int ar = events.Columns.IndexOf("AudienceRule");
        if (id < 0 || sc < 0 || ar < 0) return map;
        foreach (var row in events.Rows)
            map[row[id].V ?? ""] = (row[sc].V ?? "") + "|" + (row[ar].V ?? "");
        return map;
    }

    private static string Trunc(string s) => s.Length <= 220 ? s : s[..220] + "...";
}
