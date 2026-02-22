using AzCostPilot.Data.Entities;

namespace AzCostPilot.Api.Services;

public interface ITokenService
{
    string CreateToken(User user);
}
