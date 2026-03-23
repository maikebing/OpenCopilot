namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for Doubao (豆包) — Volcengine/ByteDance LLM API, OpenAI-compatible.
    /// </summary>
    public class DoubaoProvider : OpenAIProvider
    {
        public override string Name => "Doubao";

        public DoubaoProvider(string apiKey, string model = "doubao-pro-32k", string? proxyUrl = null)
            : base(apiKey, model, "https://ark.cn-beijing.volces.com/api/v3", proxyUrl)
        {
        }
    }
}
