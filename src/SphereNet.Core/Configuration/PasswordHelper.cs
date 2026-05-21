using System.Security.Cryptography;
using System.Text;

namespace SphereNet.Core.Configuration;

public static class PasswordHelper
{
    private const string HashPrefix = "SHA256:";

    public static string Hash(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return HashPrefix + Convert.ToHexStringLower(bytes);
    }

    public static bool Verify(string plaintext, string stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(plaintext))
            return false;

        if (stored.StartsWith(HashPrefix, StringComparison.Ordinal))
            return string.Equals(Hash(plaintext), stored, StringComparison.Ordinal);

        return stored == plaintext;
    }

    public static bool IsHashed(string stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(HashPrefix, StringComparison.Ordinal);
}
