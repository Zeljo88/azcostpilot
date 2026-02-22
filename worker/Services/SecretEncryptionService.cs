using System.Security.Cryptography;
using System.Text;

namespace AzCostPilot.Worker.Services;

public sealed class SecretEncryptionService(IConfiguration configuration) : ISecretEncryptionService
{
    private readonly byte[] _key = DeriveKey(configuration["Security:EncryptionKey"] ?? "local-dev-encryption-key-change-me");

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return cipherText;
        }

        var fullCipher = Convert.FromBase64String(cipherText);
        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = new byte[aes.BlockSize / 8];
        var cipherBytes = new byte[fullCipher.Length - iv.Length];
        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullCipher, iv.Length, cipherBytes, 0, cipherBytes.Length);

        aes.IV = iv;
        var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string keyMaterial)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }
}
