using HomeNode.Protocol;

namespace HomeNode.Services;

// The node's own keypair, held in a file in a keystore directory
// that is never the database and never the record store.
//
// Terminology, kept distinct on purpose (the settled terms for
// the backup key question):
// - This is the node content keypair (packet sealing): the node's
//   one P-256 identity keypair under the protocol contract. Telling
//   and acknowledgement packets seal to its public half; delivery
//   packets are signed with it; the same keypair serves ECDH and
//   ECDSA, with domain separation carried by the contract's
//   derivation and signature labels.
// - The household backup key (keystore) is a different thing:
//   The backup tool's symmetric keystore key for backup sealing. A backup
//   is sealed data, not a packet to a reader, so backup sealing
//   does not use or derive from this keypair, and nothing in this
//   project calls the backup key a content key or this keypair a
//   backup key.
//
// The key file holds two lines: the private scalar (base64url,
// the contract's local store encoding) and the public point
// (base64url). It is generated on first use when absent. The
// node's party id in plain packets is household-node.
public class NodeKeyStore : IDisposable
{
    public const string NodePartyId = "household-node";

    private readonly string _directory;

    public NodeKeyStore(string? directory = null)
    {
        _directory = directory
            ?? Environment.GetEnvironmentVariable("HOME_NODE_KEYSTORE")
            ?? Path.Combine(AppContext.BaseDirectory, "node-keys");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "node-content-key.txt");
        if (File.Exists(path))
        {
            // A key file written by an older build (or
            // copied in) may be group or world readable. Tighten it
            // on load, on platforms that carry file modes.
            TightenKeyFile(path);
            var lines = File.ReadAllLines(path);
            Keypair = IdentityKeypair.FromExported(lines[0].Trim(), lines[1].Trim());
        }
        else
        {
            Keypair = IdentityKeypair.Generate();
            WriteKeyFile(path, new[] { Keypair.PrivateKeyBase64Url, Keypair.PublicKeyBase64Url });
        }
    }

    // The file holds the node's private scalar, so it is
    // created owner read/write only (0600) whatever the process
    // umask says, never with the default permissions a plain
    // File.WriteAllLines would leave behind.
    private static void WriteKeyFile(string path, string[] lines)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }
        TightenKeyFile(path);
    }

    private static void TightenKeyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public IdentityKeypair Keypair { get; }
    public string PublicKeyBase64Url => Keypair.PublicKeyBase64Url;
    // The door fingerprint rule: lowercase hex SHA-256 of the 65
    // byte uncompressed public point.
    public string Fingerprint => Keypair.Fingerprint;
    public string PartyId => NodePartyId;

    public void Dispose() => Keypair.Dispose();
}
