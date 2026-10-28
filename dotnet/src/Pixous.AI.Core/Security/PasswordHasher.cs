using System.Security.Cryptography;

namespace Pixous.AI.Core.Security;

/// <summary>
/// PBKDF2 password hashing for the Settings panel gate.
/// Format: <c>pbkdf2$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>.
/// No third-party dependencies; constant-time verification.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Prefix = "pbkdf2";

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 10_000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time equality for plaintext fallbacks (config password).</summary>
    public static bool FixedEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bb = System.Text.Encoding.UTF8.GetBytes(b ?? string.Empty);
        // Compare regardless of length to avoid trivial timing leaks.
        var diff = ba.Length ^ bb.Length;
        var len = Math.Max(ba.Length, bb.Length);
        for (var i = 0; i < len; i++)
            diff |= (i < ba.Length ? ba[i] : 0) ^ (i < bb.Length ? bb[i] : 0);
        return diff == 0;
    }
}
