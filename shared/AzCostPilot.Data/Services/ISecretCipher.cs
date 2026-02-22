namespace AzCostPilot.Data.Services;

public interface ISecretCipher
{
    string Decrypt(string cipherText);
}
