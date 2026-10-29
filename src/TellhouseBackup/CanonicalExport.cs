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

    public static Export Build(string dbPath)
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
    // pre backup export and a post restore export (the package wording:
    // excluding only the restore audit entry and rotated key identifiers).
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
            var bRows = bt.Rows.Select(r => JsonSerializer.Serialize(r, JsonOpts)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var aRows = at.Rows.Select(r => JsonSerializer.Serialize(r, JsonOpts)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!bRows.SequenceEqual(aRows))
            {
                var onlyB = bRows.Except(aRows).Take(3);
                var onlyA = aRows.Except(bRows).Take(3);
                foreach (var x in onlyB) diffs.Add($"table {name}: row only before: {Trunc(x)}");
                foreach (var x in onlyA) diffs.Add($"table {name}: row only after: {Trunc(x)}");
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
