using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Offchain_Tokenize.Configuration;

namespace Offchain_Tokenize.Services
{
    public interface ICreWorkflowClient
    {
        Task<CreTriggerResult> TriggerBondIssuanceAsync(CreBondIssuanceRequest payload, CancellationToken cancellationToken = default);
    }

    public sealed class CreWorkflowClient : ICreWorkflowClient
    {
        private readonly HttpClient _httpClient;
        private readonly CreWorkflowOptions _options;

        public CreWorkflowClient(HttpClient httpClient, IOptions<CreWorkflowOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<CreTriggerResult> TriggerBondIssuanceAsync(CreBondIssuanceRequest payload, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.BondIssuanceUrl))
            {
                return new CreTriggerResult(false, "CreWorkflow:BondIssuanceUrl is not configured.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BondIssuanceUrl)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? new CreTriggerResult(true, null, body)
                : new CreTriggerResult(false, $"CRE trigger failed ({(int)response.StatusCode}): {body}", body);
        }
    }

    public sealed record CreTriggerResult(bool Success, string? Error, string? ResponseBody = null);

    public sealed class CreBondIssuanceRequest
    {
        [JsonPropertyName("issuerId")]
        public string IssuerId { get; set; } = string.Empty;
        [JsonPropertyName("isin")]
        public string Isin { get; set; } = string.Empty;
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;
        [JsonPropertyName("totalSize")]
        public string TotalSize { get; set; } = "0";
        [JsonPropertyName("faceValue")]
        public string FaceValue { get; set; } = "0";
        [JsonPropertyName("maturityDate")]
        public long MaturityDate { get; set; }
        [JsonPropertyName("conversionRatio")]
        public long ConversionRatio { get; set; }
        [JsonPropertyName("conversionPrice")]
        public long ConversionPrice { get; set; }
    }
}
