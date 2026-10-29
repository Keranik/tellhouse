using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LisaAssistant;

// Conformance checks against the shared the protocol test vectors
// (standins/vectors/test-vectors.json, copied from the frozen ws/k
// file). This code codes against the vectors from the outside,
// exactly as the contract requires of a consumer: it never calls
// the protocol library.
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

        Console.WriteLine($"SELFTEST {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
