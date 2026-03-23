using System;
using System.Collections.Generic;
using OpenCopilot.Providers;

namespace OpenCopilot.Options
{
    internal sealed class ConfiguredProviderDefinition
    {
        public ConfiguredProviderDefinition(string name, string preferredModel)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
            if (string.IsNullOrWhiteSpace(preferredModel))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(preferredModel));

            Name = name;
            PreferredModel = preferredModel;
        }

        public string Name { get; }

        public string PreferredModel { get; }
    }

    internal static class ProviderConfigurationCatalog
    {
        public static IReadOnlyList<ConfiguredProviderDefinition> GetConfiguredProviders(OpenCopilotOptions options, IEnumerable<ILlmProvider> providers)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (providers == null)
                throw new ArgumentNullException(nameof(providers));

            var configuredProviders = new List<ConfiguredProviderDefinition>();
            foreach (var provider in providers)
            {
                if (provider == null)
                    continue;

                var preferredModel = GetPreferredModel(options, provider.Name);
                if (!IsConfigured(options, provider.Name, preferredModel))
                    continue;

                configuredProviders.Add(new ConfiguredProviderDefinition(provider.Name, preferredModel));
            }

            return configuredProviders;
        }

        public static string? GetPreferredModel(OpenCopilotOptions options, string providerName)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(providerName))
                return null;

            switch (providerName)
            {
                case "OpenAI":
                    return options.OpenAIModel;
                case "DeepSeek":
                    return options.DeepSeekModel;
                case "Doubao":
                    return options.DoubaoModel;
                case "Ollama":
                    return options.OllamaModel;
                case "Docker Desktop AI":
                    return options.DockerDesktopAIModel;
                default:
                    return null;
            }
        }

        private static bool IsConfigured(OpenCopilotOptions options, string providerName, string? preferredModel)
        {
            switch (providerName)
            {
                case "OpenAI":
                    return HasValue(options.OpenAIApiKey)
                        && HasValue(options.OpenAIBaseUrl)
                        && HasValue(preferredModel);
                case "DeepSeek":
                    return HasValue(options.DeepSeekApiKey)
                        && HasValue(preferredModel);
                case "Doubao":
                    return HasValue(options.DoubaoApiKey)
                        && HasValue(preferredModel);
                case "Ollama":
                    return HasValue(options.OllamaBaseUrl)
                        && HasValue(preferredModel);
                case "Docker Desktop AI":
                    return HasValue(options.DockerDesktopAIBaseUrl)
                        && HasValue(preferredModel);
                default:
                    return false;
            }
        }

        private static bool HasValue(string? value)
            => !string.IsNullOrWhiteSpace(value);
    }
}
