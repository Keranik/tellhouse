using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TellhouseBackup;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        try
        {
            return args[0] switch
            {
                "keygen-content" => KeygenContent(args),
                "backup" => Backup(args),
                "restore" => RestoreCmd(args, "recovery-key"),
                "demo-snapshot" => Backup(args, "demo-snapshot"),
                "demo-reset" => RestoreCmd(args, "demo-reset"),
                "open-check" => OpenCheck(args),
                "verify" => Verify(args),
                "export" => ExportCmd(args),
                "seal-db" => SealDb(args),
                "unseal-db" => UnsealDb(args),
                "selftest" => SelfTest.Run(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static string Opt(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--" + name) return args[i + 1];
        throw new InvalidOperationException("missing --" + name);
    }

    private static int KeygenContent(string[] args)
    {
        var ks = ContentKeyStore.LoadOrCreate(Opt(args, "keystore"));
        Console.WriteLine("household content key ready, keyId=" + ks.KeyId + " (key material stays in the keystore file)");
        return 0;
    }

    private static int Backup(string[] args, string label = "record")
    {
        var ks = ContentKeyStore.LoadOrCreate(Opt(args, "keystore"));
        var (backup, phrase) = SealedBackup.Create(Opt(args, "db"), ks, label);
        SealedBackup.Write(backup, Opt(args, "out"));
        Console.WriteLine("sealed backup written: " + Opt(args, "out") + " backupId=" + backup.BackupId + " label=" + label);
        Console.WriteLine("RECOVERY PHRASE (shown once, held by the household, never stored by this tool):");
        Console.WriteLine(phrase);
        return 0;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains("--" + name);

    // The one restore path. Opens the backup first; only a successful open
    // reaches the restore, so a wrong key leaves no destination file.
    public static void RestoreFromPhrase(string backupPath, string phrase, string destDb, string pathLabel,
        bool overrideLiveNode = false)
    {
        var backup = SealedBackup.Read(backupPath);
        var export = SealedBackup.OpenWithPhrase(backup, phrase);
        Restore.RestoreToFreshNode(export, backup, destDb, pathLabel, overrideLiveNode);
    }

    private static int RestoreCmd(string[] args, string pathLabel)
    {
        RestoreFromPhrase(Opt(args, "backup"), Opt(args, "phrase"), Opt(args, "dest-db"), pathLabel,
            HasFlag(args, "override-live-node"));
        Console.WriteLine("restore complete onto fresh node database: " + Opt(args, "dest-db") + " path=" + pathLabel);
        return 0;
    }

    private static int OpenCheck(string[] args)
    {
        var backup = SealedBackup.Read(Opt(args, "backup"));
        if (args.Contains("--phrase"))
        {
            SealedBackup.OpenWithPhrase(backup, Opt(args, "phrase"));
            Console.WriteLine("open-check: opened with the recovery phrase");
        }
        else
        {
            var ks = ContentKeyStore.Load(Opt(args, "keystore"));
            SealedBackup.OpenWithContentKey(backup, ks);
            Console.WriteLine("open-check: opened with the household content key");
        }
        return 0;
    }

    private static int Verify(string[] args)
    {
        var before = CanonicalExport.Build(Opt(args, "before"));
        var after = CanonicalExport.Build(Opt(args, "after"));
        var result = CanonicalExport.Compare(before, after);
        Console.WriteLine($"verify: tables={result.TablesCompared} rows={result.RowsCompared} audienceRows={result.AudienceRowsCompared} equal={result.Equal}");
        foreach (var d in result.Diffs) Console.WriteLine("diff: " + d);
        return result.Equal ? 0 : 1;
    }

    private static int ExportCmd(string[] args)
    {
        var export = CanonicalExport.Build(Opt(args, "db"));
        File.WriteAllText(Opt(args, "out"), CanonicalExport.Serialize(export));
        Console.WriteLine("canonical export written: " + Opt(args, "out"));
        return 0;
    }

    private static int SealDb(string[] args)
    {
        var ks = ContentKeyStore.Load(Opt(args, "keystore"));
        AtRestFileSeal.SealFile(Opt(args, "db"), Opt(args, "out"), ks, HasFlag(args, "override-live-node"));
        Console.WriteLine("database file sealed at rest: " + Opt(args, "out"));
        return 0;
    }

    private static int UnsealDb(string[] args)
    {
        var ks = ContentKeyStore.Load(Opt(args, "keystore"));
        AtRestFileSeal.UnsealFile(Opt(args, "sealed"), Opt(args, "dest-db"), ks, HasFlag(args, "override-live-node"));
        Console.WriteLine("database file unsealed: " + Opt(args, "dest-db"));
        return 0;
    }

    private static int Unknown(string cmd) { Console.WriteLine("unknown command: " + cmd); PrintUsage(); return 2; }

    private static void PrintUsage()
    {
        Console.WriteLine("TellhouseBackup commands:");
        Console.WriteLine("  keygen-content --keystore <file>");
        Console.WriteLine("  backup --db <file> --keystore <file> --out <file>   (prints the recovery phrase once)");
        Console.WriteLine("  demo-snapshot --db <file> --keystore <file> --out <file>");
        Console.WriteLine("  restore --backup <file> --phrase \"<words>\" --dest-db <file> [--override-live-node]");
        Console.WriteLine("  demo-reset --backup <file> --phrase \"<words>\" --dest-db <file> [--override-live-node]");
        Console.WriteLine("  open-check --backup <file> --keystore <file> | --phrase \"<words>\"");
        Console.WriteLine("  verify --before <db> --after <db>");
        Console.WriteLine("  export --db <file> --out <file>");
        Console.WriteLine("  seal-db --db <file> --keystore <file> --out <file> [--override-live-node]");
        Console.WriteLine("  unseal-db --sealed <file> --keystore <file> --dest-db <file> [--override-live-node]");
        Console.WriteLine("  (--override-live-node forces a file swap a live node is holding, and logs the override beside the database)");
        Console.WriteLine("  selftest [--workdir <dir>]");
    }
}
