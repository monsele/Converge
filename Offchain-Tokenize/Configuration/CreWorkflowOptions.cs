namespace Offchain_Tokenize.Configuration
{
    public class CreWorkflowOptions
    {
        public const string SectionName = "CreWorkflow";

        public string BondIssuanceUrl { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string ApiKeyHeaderName { get; set; } = "X-API-Key";
    }
}
