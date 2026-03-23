namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for DeepSeek API — OpenAI-compatible endpoint.
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        public override string Name => "DeepSeek";

        public DeepSeekProvider(string apiKey, string model = "deepseek-chat", string? proxyUrl = null)
            : base(apiKey, model, "https://api.deepseek.com/v1", proxyUrl)
        {
        }
    }
}
