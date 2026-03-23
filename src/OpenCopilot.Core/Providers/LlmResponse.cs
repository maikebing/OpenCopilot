namespace OpenCopilot.Providers
{
    public class LlmResponse
    {
        public string Content { get; set; }
        public string FinishReason { get; set; }
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }

        public static LlmResponse Success(
            string content,
            string finishReason = "stop",
            int promptTokens = 0,
            int completionTokens = 0)
            => new LlmResponse
            {
                Content = content,
                FinishReason = finishReason,
                IsSuccess = true,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };

        public static LlmResponse Failure(string errorMessage)
            => new LlmResponse { IsSuccess = false, ErrorMessage = errorMessage };
    }
}
