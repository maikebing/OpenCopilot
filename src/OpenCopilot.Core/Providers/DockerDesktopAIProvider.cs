namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for Docker Desktop AI — OpenAI-compatible endpoint on localhost:12434.
    /// </summary>
    public class DockerDesktopAIProvider : OpenAIProvider
    {
        public override string Name => "Docker Desktop AI";

        public DockerDesktopAIProvider(string model = "ai/smollm2",
            string baseUrl = "http://localhost:12434/engines/llama.cpp/v1",
            string? proxyUrl = null)
            : base(string.Empty, model, baseUrl, proxyUrl)
        {
        }
    }
}
