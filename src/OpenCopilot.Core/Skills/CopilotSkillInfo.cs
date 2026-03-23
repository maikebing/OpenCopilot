namespace OpenCopilot.Skills
{
    public enum CopilotSkillFileType
    {
        Instructions,    // .github/copilot-instructions.md
        MemoryFile,      // *memory*.md, .copilot/memory/*.md
        SkillDefinition, // .copilot/skills/*.md or *.json
        PromptFile,      // .github/prompts/*.prompt.md
        Unknown
    }

    public class CopilotSkillInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public CopilotSkillFileType FileType { get; set; }
        public string? Content { get; set; }
        public long FileSizeBytes { get; set; }
        public System.DateTime LastModified { get; set; }
    }
}
