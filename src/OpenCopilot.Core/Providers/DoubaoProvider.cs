namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for Doubao (豆包) — Volcengine/ByteDance LLM API, OpenAI-compatible.
    /// </summary>
    public class DoubaoProvider : OpenAIProvider
    {
        public override string Name => "Doubao";

        public override string[] AvailableModels => new[]
        {
            "doubao-pro-4k",
            "doubao-pro-32k",
            "doubao-pro-128k",
            "doubao-lite-4k",
            "doubao-lite-32k",
            "doubao-lite-128k"
        };

        public DoubaoProvider(string apiKey, string model = "doubao-pro-32k", string? proxyUrl = null)
            : base(apiKey, model, "https://ark.cn-beijing.volces.com/api/v3", proxyUrl)
        {
        }
    }
}
