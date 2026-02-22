using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AzCostPilot.Api.Services;

public sealed class AzureSubscriptionDiscoveryService(HttpClient httpClient) : IAzureSubscriptionDiscoveryService
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<IReadOnlyList<AzureSubscriptionInfo>> ListSubscriptionsAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await GetAccessTokenAsync(tenantId, clientId, clientSecret, cancellationToken);
            var endpoint = "https://management.azure.com/subscriptions?api-version=2020-01-01";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new AzureConnectionValidationException(
                    $"Azure subscription query failed ({(int)response.StatusCode}). {Truncate(errorBody)}");
            }

            var payload = await response.Content.ReadFromJsonAsync<SubscriptionsResponse>(cancellationToken: cancellationToken);
            var subscriptions = payload?.Value?
                .Where(x => !string.IsNullOrWhiteSpace(x.SubscriptionId))
                .Select(x => new AzureSubscriptionInfo(
                    x.SubscriptionId.Trim(),
                    x.DisplayName?.Trim() ?? x.SubscriptionId.Trim(),
                    x.State?.Trim() ?? "Unknown"))
                .DistinctBy(x => x.SubscriptionId, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];

            return subscriptions;
        }
        catch (AzureConnectionValidationException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AzureConnectionValidationException($"Azure endpoints are unreachable. {ex.Message}");
        }
    }

    private async Task<string> GetAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://management.azure.com/.default"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AzureConnectionValidationException(
                $"Azure authentication failed ({(int)response.StatusCode}). {Truncate(errorBody)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
        {
            throw new AzureConnectionValidationException("Azure authentication succeeded but no access token was returned.");
        }

        return payload.AccessToken;
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No details returned.";
        }

        return value.Length <= 240 ? value : $"{value[..240]}...";
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record SubscriptionsResponse(List<SubscriptionPayload> Value);

    private sealed record SubscriptionPayload(string SubscriptionId, string DisplayName, string State);
}

public sealed class AzureConnectionValidationException(string message) : Exception(message);
