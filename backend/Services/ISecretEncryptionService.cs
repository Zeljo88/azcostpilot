namespace AzCostPilot.Api.Services;

public interface ISecretEncryptionService
{
    string Encrypt(string plainText);

    string Decrypt(string cipherText);
}
