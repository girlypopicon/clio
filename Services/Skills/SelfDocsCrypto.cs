using System.Security.Cryptography;
using System.Text;

namespace CLIo.Services.Skills;

/// <summary>
/// Reads the architecture reference that the build encrypted (see the EncryptSelfDocs target in CLIo.csproj).
/// Format: "CSD1" | 16-byte IV | AES-256-CBC ciphertext | 32-byte HMAC-SHA256 over everything before it.
/// The text is decrypted in memory only and never written to disk.
///
/// What this protects: the reference does not appear as readable text inside the shipped program, and a
/// tampered or wrong-key blob is rejected. What it does not protect: the program has to be able to read it,
/// so the key is in the program too. Someone willing to reverse-engineer the app can get it back. The
/// reference is written to contain nothing sensitive for that reason.
/// </summary>
internal static class SelfDocsCrypto
{
    private static readonly byte[] Magic = "CSD1"u8.ToArray();
    private const int IvLength = 16;
    private const int MacLength = 32;

    public static string? TryDecrypt(byte[] blob)
    {
        try
        {
            if (blob.Length < Magic.Length + IvLength + MacLength + 16 || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                return null;
            }

            var phrase = SelfDocsKey.Phrase();
            var encKey = SHA256.HashData(Encoding.UTF8.GetBytes(phrase + "|enc"));
            var macKey = SHA256.HashData(Encoding.UTF8.GetBytes(phrase + "|mac"));

            var body = blob.AsSpan(0, blob.Length - MacLength);
            var expected = HMACSHA256.HashData(macKey, body);
            if (!CryptographicOperations.FixedTimeEquals(expected, blob.AsSpan(blob.Length - MacLength)))
            {
                return null;
            }

            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = blob.AsSpan(Magic.Length, IvLength).ToArray();
            var cipher = blob.AsSpan(Magic.Length + IvLength, blob.Length - Magic.Length - IvLength - MacLength);
            return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, aes.IV));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
