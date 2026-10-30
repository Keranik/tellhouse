using System.Security.Cryptography;
using System.Text;

namespace HomeNode.Services;

// The house side's credential check, in one place, so every
// surface that performs a house-side act applies the same
// authorization: the HTTP door and /admin adapter in Program.cs
// and the Connections page, which performs the same ceremony.
// The secret is the node's own, set through the
// HOME_NODE_HOUSE_SECRET environment variable or the
// Door:HouseSecret configuration value, and held only by whoever
// operates the house's side of the door. Without it configured,
// nothing authorizes: a node with no house secret has no house
// side, and failing closed is the honest answer. The comparison
// hashes both sides and compares in fixed time, so the check
// itself does not leak the secret's shape.
public sealed class HouseAuthorization
{
    private readonly string? _secret;

    public HouseAuthorization(string? secret)
    {
        _secret = secret;
    }

    public bool Authorizes(string? presented)
    {
        if (string.IsNullOrEmpty(_secret) || string.IsNullOrEmpty(presented))
        {
            return false;
        }
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(_secret));
        return CryptographicOperations.FixedTimeEquals(presentedHash, secretHash);
    }
}
