using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;

namespace SphereNet.Network.Encryption;

/// <summary>Backend of the BCRYPTHASH / BCRYPTVALIDATE script functions
/// (Source-X CBCrypt over Openwall crypt_blowfish, non-glibc build).</summary>
public static class ScriptBCrypt
{
    /// <summary>CBCrypt::HashBCrypt: prefix code 0 = $2a$, 1 = $2b$, 2 = $2y$, anything
    /// else outside 3/4 falls back to $2a$. Codes 3 ($1$) and 4 (_) generate a salt but
    /// crypt_rn is blowfish-only in that build, so the hash comes back empty.
    /// <paramref name="cost"/> is already clamped to 4..31 by the caller.</summary>
    public static string Hash(string password, int prefixCode, int cost)
    {
        string version = prefixCode switch
        {
            1 => "2b",
            2 => "2y",
            3 or 4 => "",
            _ => "2a",
        };
        if (version.Length == 0 || cost < 4 || cost > 31)
            return "";
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            return OpenBsdBCrypt.Generate(version, password.ToCharArray(), salt, cost);
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>CBCrypt::ValidateBCrypt: re-hash with the stored setting and compare;
    /// a setting crypt_blowfish does not accept ($2a$/$2b$/$2x$/$2y$ only) fails.</summary>
    public static bool Validate(string password, string hash)
    {
        if (hash.Length < 7 || hash[0] != '$' || hash[1] != '2' || hash[3] != '$' ||
            (hash[2] != 'a' && hash[2] != 'b' && hash[2] != 'x' && hash[2] != 'y'))
            return false;
        try
        {
            return OpenBsdBCrypt.CheckPassword(hash, password.ToCharArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
