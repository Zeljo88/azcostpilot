namespace AzCostPilot.Worker.Services;

public interface ISecretEncryptionService
{
    string Decrypt(string cipherText);
}
