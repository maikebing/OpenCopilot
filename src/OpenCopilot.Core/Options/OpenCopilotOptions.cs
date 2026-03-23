namespace OpenCopilot.Options
{
    /// <summary>
    /// Persisted settings for the OpenCopilot extension.
    /// </summary>
    public class OpenCopilotOptions
    {
        // ── Active provider ──────────────────────────────────────────────────
        public string ActiveProvider { get; set; } = "OpenAI";

        // ── OpenAI ───────────────────────────────────────────────────────────
        public string OpenAIApiKey { get; set; } = "";
        public string OpenAIModel { get; set; } = "gpt-4o-mini";
        public string OpenAIBaseUrl { get; set; } = "https://api.openai.com/v1";

        // ── DeepSeek ─────────────────────────────────────────────────────────
        public string DeepSeekApiKey { get; set; } = "";
        public string DeepSeekModel { get; set; } = "deepseek-chat";

        // ── Doubao (豆包) ────────────────────────────────────────────────────
        public string DoubaoApiKey { get; set; } = "";
        public string DoubaoModel { get; set; } = "doubao-pro-32k";

        // ── Ollama ───────────────────────────────────────────────────────────
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "codellama";

        // ── Docker Desktop AI ────────────────────────────────────────────────
        public string DockerDesktopAIBaseUrl { get; set; } = "http://localhost:12434/engines/llama.cpp/v1";
        public string DockerDesktopAIModel { get; set; } = "ai/smollm2";

        // ── Completion behaviour ─────────────────────────────────────────────
        public bool EnableInlineCompletion { get; set; } = true;
        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 512;

        // ── Proxy ────────────────────────────────────────────────────────────
        /// <summary>
        /// Route all cloud LLM API calls through a proxy (e.g. Clash, V2Ray).
        /// Localhost providers (Ollama, Docker Desktop AI) are never proxied.
        /// </summary>
        public bool UseProxy { get; set; } = false;

        /// <summary>HTTP proxy URL, e.g. <c>http://127.0.0.1:7890</c>.</summary>
        public string ProxyUrl { get; set; } = "http://127.0.0.1:7890";

        // ── MCP ───────────────────────────────────────────────────────────────
        /// <summary>Enable the built-in Bing web-search MCP tool.</summary>
        public bool McpEnableWebSearch { get; set; } = false;

        /// <summary>
        /// Bing Web Search v7 API key (Azure Cognitive Services / Bing Search resource).
        /// Required when <see cref="McpEnableWebSearch"/> is <see langword="true"/>.
        /// </summary>
        public string BingSearchApiKey { get; set; } = "";

        /// <summary>
        /// Comma-separated names of MCP servers that are disabled (excluded from tool calls).
        /// Built-in tools are always enabled regardless of this setting.
        /// Example: <c>"filesystem,my-custom-server"</c>
        /// </summary>
        public string DisabledMcpServers { get; set; } = "";

        // ── UI ───────────────────────────────────────────────────────────────
        public bool ShowStatusBarInfo { get; set; } = true;
    }
}
