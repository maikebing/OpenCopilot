using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace OpenCopilot.Options
{
    /// <summary>
    /// Tools > Options > OpenCopilot settings page.
    /// </summary>
    public class OpenCopilotOptionsPage : DialogPage
    {
        private OpenCopilotOptions _options = new OpenCopilotOptions();

        /// <summary>Raised after the user clicks OK in Tools > Options.</summary>
        public event EventHandler SettingsChanged;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
                SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Active provider ──────────────────────────────────────────────────

        [Category("General")]
        [DisplayName("Active Provider")]
        [Description("The LLM provider to use for all AI features. Options: OpenAI, DeepSeek, Doubao, Ollama, Docker Desktop AI")]
        public string ActiveProvider
        {
            get => _options.ActiveProvider;
            set => _options.ActiveProvider = value;
        }

        [Category("General")]
        [DisplayName("Enable Inline Completion")]
        [Description("Automatically suggest code completions as you type.")]
        public bool EnableInlineCompletion
        {
            get => _options.EnableInlineCompletion;
            set => _options.EnableInlineCompletion = value;
        }

        [Category("General")]
        [DisplayName("Temperature")]
        [Description("Creativity of completions (0.0 = deterministic, 1.0 = very creative). Recommended: 0.2 for code.")]
        public double Temperature
        {
            get => _options.Temperature;
            set => _options.Temperature = value;
        }

        [Category("General")]
        [DisplayName("Max Tokens")]
        [Description("Maximum number of tokens in the completion response.")]
        public int MaxTokens
        {
            get => _options.MaxTokens;
            set => _options.MaxTokens = value;
        }

        [Category("General")]
        [DisplayName("Show Status Bar Info")]
        [Description("Display active provider and model name in the Visual Studio status bar.")]
        public bool ShowStatusBarInfo
        {
            get => _options.ShowStatusBarInfo;
            set => _options.ShowStatusBarInfo = value;
        }

        // ── OpenAI ───────────────────────────────────────────────────────────

        [Category("OpenAI")]
        [DisplayName("API Key")]
        [Description("Your OpenAI API key (starts with sk-).")]
        [PasswordPropertyText(true)]
        public string OpenAIApiKey
        {
            get => _options.OpenAIApiKey;
            set => _options.OpenAIApiKey = value;
        }

        [Category("OpenAI")]
        [DisplayName("Model")]
        [Description("OpenAI model to use, e.g. gpt-4o-mini, gpt-4o, gpt-4-turbo.")]
        public string OpenAIModel
        {
            get => _options.OpenAIModel;
            set => _options.OpenAIModel = value;
        }

        [Category("OpenAI")]
        [DisplayName("Base URL")]
        [Description("Override the OpenAI API base URL for compatible providers.")]
        public string OpenAIBaseUrl
        {
            get => _options.OpenAIBaseUrl;
            set => _options.OpenAIBaseUrl = value;
        }

        // ── DeepSeek ─────────────────────────────────────────────────────────

        [Category("DeepSeek")]
        [DisplayName("API Key")]
        [Description("Your DeepSeek API key.")]
        [PasswordPropertyText(true)]
        public string DeepSeekApiKey
        {
            get => _options.DeepSeekApiKey;
            set => _options.DeepSeekApiKey = value;
        }

        [Category("DeepSeek")]
        [DisplayName("Model")]
        [Description("DeepSeek model: deepseek-chat, deepseek-coder, deepseek-reasoner.")]
        public string DeepSeekModel
        {
            get => _options.DeepSeekModel;
            set => _options.DeepSeekModel = value;
        }

        // ── Doubao ───────────────────────────────────────────────────────────

        [Category("Doubao (豆包)")]
        [DisplayName("API Key")]
        [Description("Your Volcengine/Doubao API key.")]
        [PasswordPropertyText(true)]
        public string DoubaoApiKey
        {
            get => _options.DoubaoApiKey;
            set => _options.DoubaoApiKey = value;
        }

        [Category("Doubao (豆包)")]
        [DisplayName("Model")]
        [Description("Doubao model: doubao-pro-4k, doubao-pro-32k, doubao-pro-128k, doubao-lite-4k.")]
        public string DoubaoModel
        {
            get => _options.DoubaoModel;
            set => _options.DoubaoModel = value;
        }

        // ── Ollama ───────────────────────────────────────────────────────────

        [Category("Ollama (Local)")]
        [DisplayName("Base URL")]
        [Description("Ollama server URL. Default: http://localhost:11434")]
        public string OllamaBaseUrl
        {
            get => _options.OllamaBaseUrl;
            set => _options.OllamaBaseUrl = value;
        }

        [Category("Ollama (Local)")]
        [DisplayName("Model")]
        [Description("Ollama model tag, e.g. codellama, llama3, mistral.")]
        public string OllamaModel
        {
            get => _options.OllamaModel;
            set => _options.OllamaModel = value;
        }

        // ── Docker Desktop AI ────────────────────────────────────────────────

        [Category("Docker Desktop AI (Local)")]
        [DisplayName("Base URL")]
        [Description("Docker Desktop AI API base URL. Default: http://localhost:12434/engines/llama.cpp/v1")]
        public string DockerDesktopAIBaseUrl
        {
            get => _options.DockerDesktopAIBaseUrl;
            set => _options.DockerDesktopAIBaseUrl = value;
        }

        [Category("Docker Desktop AI (Local)")]
        [DisplayName("Model")]
        [Description("Docker Desktop AI model, e.g. ai/smollm2, ai/llama3.2.")]
        public string DockerDesktopAIModel
        {
            get => _options.DockerDesktopAIModel;
            set => _options.DockerDesktopAIModel = value;
        }

        // ── Serialization ────────────────────────────────────────────────────

        /// <summary>Returns a snapshot of the current settings.</summary>
        public OpenCopilotOptions GetOptions() => new OpenCopilotOptions
        {
            ActiveProvider = ActiveProvider,
            EnableInlineCompletion = EnableInlineCompletion,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            ShowStatusBarInfo = ShowStatusBarInfo,
            OpenAIApiKey = OpenAIApiKey,
            OpenAIModel = OpenAIModel,
            OpenAIBaseUrl = OpenAIBaseUrl,
            DeepSeekApiKey = DeepSeekApiKey,
            DeepSeekModel = DeepSeekModel,
            DoubaoApiKey = DoubaoApiKey,
            DoubaoModel = DoubaoModel,
            OllamaBaseUrl = OllamaBaseUrl,
            OllamaModel = OllamaModel,
            DockerDesktopAIBaseUrl = DockerDesktopAIBaseUrl,
            DockerDesktopAIModel = DockerDesktopAIModel
        };
    }
}
