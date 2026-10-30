using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LisaAssistant;

// Conformance checks against the shared test vectors
// (standins/vectors/test-vectors.json, copied from the frozen
// contract's file). This code codes against the vectors from the
// outside, exactly as the contract requires of a later
// implementation: it never calls the protocol library.
public static class Selftest
{
    public static int Run(string vectorsPath)
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (ok) pass++; else fail++;
        }

        var root = JsonNode.Parse(File.ReadAllText(vectorsPath))!.AsObject();
        Check("vectors file is contract version 1.0",
            root["contract_version"]!.GetValue<string>() == "1.0");

        var pubs = new Dictionary<string, string>();
        var privs = new Dictionary<string, string>();
        var hh = root["household"]!.AsObject();
        pubs[hh["node_party_id"]!.GetValue<string>()] = hh["node_public_key"]!.GetValue<string>();
        privs[hh["node_party_id"]!.GetValue<string>()] = hh["node_private_key_test_only"]!.GetValue<string>();
        foreach (var m in root["members"]!.AsArray())
        {
            var o = m!.AsObject();
            pubs[o["party_id"]!.GetValue<string>()] = o["public_key"]!.GetValue<string>();
            privs[o["party_id"]!.GetValue<string>()] = o["private_key_test_only"]!.GetValue<string>();
        }
        (string x, string y) XY(string party) => Protocol.XYFromPubRaw(Protocol.UnB64Url(pubs[party]));

        foreach (var vNode in root["vectors"]!.AsArray())
        {
            var v = vNode!.AsObject();
            var name = v["name"]!.GetValue<string>();
            var envelope = v["envelope"]!.AsObject();
            Check(name + ": canonical envelope reproduces",
                Protocol.Canonical(envelope) == v["envelope_canonical"]!.GetValue<string>());
            var plainParsed = JsonNode.Parse(v["plaintext_canonical"]!.GetValue<string>());
            Check(name + ": canonical plaintext reproduces",
                Protocol.Canonical(plainParsed) == v["plaintext_canonical"]!.GetValue<string>());
            var plain = plainParsed!.AsObject();
            var sender = plain["sender_id"]!.GetValue<string>();
            var recipient = plain["recipient_id"]!.GetValue<string>();
            var wire = new JsonObject
            {
                ["ciphertext"] = v["ciphertext"]!.GetValue<string>(),
                ["envelope"] = envelope.DeepClone(),
                ["signature"] = v["signature"]!.GetValue<string>()
            };
            Check(name + ": full wire packet canonical reproduces",
                Protocol.Canonical(wire) == v["packet_canonical"]!.GetValue<string>());
            var (sx, sy) = XY(sender);
            Check(name + ": vector signature verifies under the sender key",
                Protocol.VerifyPacket(sx, sy, wire));
            // Seal reproduction, byte for byte, from the seal inputs.
            var seal = v["seal"]!.AsObject();
            var reproduced = Protocol.Seal(
                Protocol.UnB64Url(pubs[recipient]),
                Encoding.UTF8.GetBytes(v["plaintext_canonical"]!.GetValue<string>()),
                Protocol.AadBytes(envelope),
                seal["ephemeral_private_key_test_only"]!.GetValue<string>(),
                Protocol.UnB64Url(seal["ephemeral_public_key"]!.GetValue<string>()),
                Protocol.UnB64Url(seal["nonce"]!.GetValue<string>()));
            Check(name + ": ciphertext reproduces byte for byte",
                Protocol.B64Url(reproduced) == v["ciphertext"]!.GetValue<string>());
            Check(name + ": ciphertext SHA-256 matches",
                Protocol.Hex(SHA256.HashData(reproduced)) == v["ciphertext_sha256_hex"]!.GetValue<string>());
            // Opening under the recipient test key yields the plaintext.
            var (rx, ry) = XY(recipient);
            string opened = "";
            try
            {
                opened = Protocol.Canonical(JsonNode.Parse(Encoding.UTF8.GetString(
                    Protocol.OpenPacket(privs[recipient], rx, ry, wire))));
            }
            catch { }
            Check(name + ": opens under the recipient key to the vector plaintext",
                opened == v["plaintext_canonical"]!.GetValue<string>());
        }

        // Per reader sealing: the Daniel delivery must not open
        // under Lisa's key.
        var delivery = root["vectors"]!.AsArray()
            .Select(n => n!.AsObject()).First(o => o["name"]!.GetValue<string>() == "delivery_node_to_daniel");
        var dwire = new JsonObject
        {
            ["ciphertext"] = delivery["ciphertext"]!.GetValue<string>(),
            ["envelope"] = delivery["envelope"]!.DeepClone(),
            ["signature"] = delivery["signature"]!.GetValue<string>()
        };
        var (lx, ly) = XY("member-lisa");
        bool wrongThrew = false;
        try { Protocol.OpenPacket(privs["member-lisa"], lx, ly, dwire); }
        catch { wrongThrew = true; }
        Check("delivery sealed to Daniel does not open under Lisa key", wrongThrew);

        // Session challenge proof (contract section 7).
        var sc = root["session_challenge"]!.AsObject();
        var transcript = Protocol.Transcript(sc["connection_id"]!.GetValue<string>(),
            Protocol.UnB64Url(sc["client_nonce"]!.GetValue<string>()),
            Protocol.UnB64Url(sc["server_nonce"]!.GetValue<string>()));
        Check("session challenge transcript matches the vector hex",
            Protocol.Hex(transcript) == sc["transcript_hex"]!.GetValue<string>());
        var (dx, dy) = XY("member-daniel");
        Check("session challenge proof signature verifies",
            Protocol.Verify(dx, dy, transcript,
                sc["proof"]!["proof_signature"]!.GetValue<string>()));

        // Own round trip with fresh keys and fresh packets.
        var (md, mx, my) = Protocol.NewKeyPair();
        var (od, ox, oy) = Protocol.NewKeyPair();
        JsonObject Plain(string nonce) => new()
        {
            ["body"] = new JsonObject { ["kind"] = "raw_telling", ["text"] = "round trip words" },
            ["connection_id"] = "conn-self",
            ["packet_id"] = "pkt-self",
            ["packet_nonce"] = nonce,
            ["packet_type"] = "telling",
            ["recipient_id"] = "household-node",
            ["sender_id"] = "member-self",
            ["told_at"] = 1793275200L
        };
        var w1 = Protocol.BuildPacket(md, mx, my, Protocol.PubRaw(ox, oy), Plain("QUJDREVGR0hJSktMTU5PUA"),
            "conn-self", "session", "session", "hh-demo-01", "pkt-self", 1793275200L);
        Check("own packet signature verifies", Protocol.VerifyPacket(mx, my, w1));
        Check("own packet opens at the recipient",
            Protocol.Canonical(JsonNode.Parse(Encoding.UTF8.GetString(
                Protocol.OpenPacket(od, ox, oy, w1)))) == Protocol.Canonical(Plain("QUJDREVGR0hJSktMTU5PUA")));
        var w2 = Protocol.BuildPacket(md, mx, my, Protocol.PubRaw(ox, oy), Plain("UFFSU1RVVldYWVpbXF1eXw"),
            "conn-self", "session", "session", "hh-demo-01", "pkt-self", 1793275200L);
        Check("same sender sealing fresh packets yields different ciphertext",
            w1["ciphertext"]!.GetValue<string>() != w2["ciphertext"]!.GetValue<string>());

        // Session hunt challenge choice (the answer must pair
        // with the challenge the node actually holds open, never
        // with a leftover from an interrupted attempt).
        SessionHunt.Attempt Att(string nonce, long at) => new(nonce, at);
        SessionHunt.Candidate Cand(string id, string server, long at) => new(id, server, at);
        var only = SessionHunt.Choose(
            new[] { Att("n1", 1000) },
            new[] { Cand("pkt-a", "s1", 1002) }, 1003);
        Check("session hunt: the only challenge is chosen and pairs with its attempt",
            only != null && only.PacketId == "pkt-a" && only.ClientNonce == "n1"
            && only.ServerNonce == "s1");
        var staleOnly = SessionHunt.Choose(
            new[] { Att("n1", 1000), Att("n2", 1010) },
            new[] { Cand("pkt-a", "s1", 1002) }, 1012);
        Check("session hunt: a challenge from a superseded attempt alone is not answered",
            staleOnly == null);
        var mixed = SessionHunt.Choose(
            new[] { Att("n1", 1000), Att("n2", 1010) },
            new[] { Cand("pkt-a", "s1", 1002), Cand("pkt-b", "s2", 1012) }, 1013);
        Check("session hunt: with a stale and a current challenge queued, the current one is chosen and paired with the latest attempt",
            mixed != null && mixed.PacketId == "pkt-b" && mixed.ClientNonce == "n2"
            && mixed.ServerNonce == "s2");
        var twoLive = SessionHunt.Choose(
            new[] { Att("n1", 1000), Att("n2", 1003) },
            new[] { Cand("pkt-a", "s1", 1005), Cand("pkt-b", "s2", 1007) }, 1008);
        Check("session hunt: the most recently issued answerable challenge wins",
            twoLive != null && twoLive.PacketId == "pkt-b" && twoLive.ClientNonce == "n2");
        var sameSecond = SessionHunt.Choose(
            new[] { Att("n1", 1000), Att("n2", 1000) },
            new[] { Cand("pkt-a", "s1", 1001), Cand("pkt-b", "s2", 1001) }, 1002);
        Check("session hunt: two knocks in one second pair the newest challenge with the newest knock, never across the two",
            sameSecond != null && sameSecond.PacketId == "pkt-b" && sameSecond.ClientNonce == "n2"
            && sameSecond.ServerNonce == "s2");
        var expired = SessionHunt.Choose(
            new[] { Att("n1", 1000) },
            new[] { Cand("pkt-a", "s1", 1002) }, 1002 + 111);
        Check("session hunt: a challenge past its lifetime is not answered",
            expired == null);
        var stillLive = SessionHunt.Choose(
            new[] { Att("n1", 1000) },
            new[] { Cand("pkt-a", "s1", 1002) }, 1002 + 100);
        Check("session hunt: a challenge inside its lifetime is answered",
            stillLive != null && stillLive.PacketId == "pkt-a");


        // Session freshness: the re-open rule the
        // auto session path runs before any session class send.
        Check("session freshness: a proof 19 minutes old is fresh",
            SessionFreshness.IsFresh(10000, 10000 + 19 * 60));
        Check("session freshness: a proof 31 minutes old is stale and must be re-opened before sending",
            SessionFreshness.IsStale(10000, 10000 + 31 * 60));
        Check("session freshness: no recorded proof is stale, never fresh",
            SessionFreshness.IsStale(0, 10000) && !SessionFreshness.IsFresh(0, 10000));
        // The key file writer creates owner only (0600)
        // files, and the tighten step repairs a file an older build
        // left readable, on platforms that carry file modes.
        if (!OperatingSystem.IsWindows())
        {
            var keyDir = Path.Combine(Path.GetTempPath(), "standin-keyfile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(keyDir);
            var keyPath = Path.Combine(keyDir, "keys.json");
            Protocol.WriteKeyFile(keyPath, "{\"d\":\"test-only\"}");
            Check("a freshly written key file is owner read/write only (0600)",
                File.GetUnixFileMode(keyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            File.SetUnixFileMode(keyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Protocol.TightenKeyFile(keyPath);
            Check("a key file left group/world readable is tightened to 0600 on load",
                File.GetUnixFileMode(keyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            // The state file holds the live connection credential
            // and is written through the same writer (SaveState in
            // Program.cs) and tightened on load the same way. It
            // used to be written with default modes, 0644 under a
            // typical umask, credential readable by anyone local.
            var statePath = Path.Combine(keyDir, "state.json");
            Protocol.WriteKeyFile(statePath, "{\"credential\":\"test-only\"}");
            Check("state file: a freshly written state file is owner read/write only (0600)",
                File.GetUnixFileMode(statePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            File.SetUnixFileMode(statePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Protocol.TightenKeyFile(statePath);
            Check("state file: a state file left group/world readable is tightened to 0600 on load",
                File.GetUnixFileMode(statePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            try { Directory.Delete(keyDir, true); } catch (IOException) { }
        }

        Console.WriteLine($"SELFTEST {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
