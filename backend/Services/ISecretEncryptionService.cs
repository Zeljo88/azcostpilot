using AzCostPilot.Data.Services;

namespace AzCostPilot.Api.Services;

public interface ISecretEncryptionService : ISecretCipher
{
    string Encrypt(string plainText);
}
