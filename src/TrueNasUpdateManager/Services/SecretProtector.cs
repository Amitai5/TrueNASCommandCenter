using System.Security.Cryptography;
using System.Text;
using TrueNasUpdateManager.Data;

namespace TrueNasUpdateManager.Services;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string AdditionalData = "TrueNasUpdateManager:v1";
    private readonly byte[] key;

    public AesGcmSecretProtector(DataPathOptions dataPath, IConfiguration configuration)
    {
        key = LoadOrCreateKey(dataPath.Path, configuration["APP_ENCRYPTION_KEY"]);
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(
            nonce,
            plaintextBytes,
            ciphertext,
            tag,
            Encoding.UTF8.GetBytes(AdditionalData));

        return string.Join(
            '.',
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);

        try
        {
            var parts = protectedValue.Split('.');
            if (parts.Length != 3)
            {
                throw new CryptographicException("Encrypted secret has an invalid format.");
            }

            var nonce = Convert.FromBase64String(parts[0]);
            var tag = Convert.FromBase64String(parts[1]);
            var ciphertext = Convert.FromBase64String(parts[2]);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                Encoding.UTF8.GetBytes(AdditionalData));

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new CryptographicException("The stored secret could not be decrypted.", exception);
        }
    }

    private static byte[] LoadOrCreateKey(string dataPath, string? configuredKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            try
            {
                var decoded = Convert.FromBase64String(configuredKey);
                if (decoded.Length != 32)
                {
                    throw new InvalidOperationException("APP_ENCRYPTION_KEY must decode to exactly 32 bytes.");
                }

                return decoded;
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("APP_ENCRYPTION_KEY must be a base64-encoded 32-byte value.", exception);
            }
        }

        Directory.CreateDirectory(dataPath);
        var keyPath = Path.Combine(dataPath, ".encryption-key");
        if (File.Exists(keyPath))
        {
            var stored = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            if (stored.Length != 32)
            {
                throw new InvalidOperationException("The local encryption key is invalid.");
            }

            return stored;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyPath, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}
