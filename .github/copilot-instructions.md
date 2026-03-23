# Copilot Instructions

## Project Guidelines
- User prefers Visual Studio extensibility work to reference and learn from `microsoft/VSExtensibility`, `microsoft/VSSDK-Extensibility-Samples`, and Mads Kristensen's Visual Studio extension projects; use those repositories as primary examples and analyze Mads Kristensen's code when similar VS extension issues arise.
- Clean up unused theme resources like `ThemedSecondaryBorderBrush` and consolidate XAML theme resources into a tighter, unified set.

## IDE Context Prioritization
- Automatically inspect IDE context first, prioritizing the current project and active document.
- Strongly prioritize repository AI instruction/memory files (Copilot, Codex, Claude, Gemini, OpenCode, OpenClaw, OpenCopilot).

## Agent Behavior
- Constrain the agent to write code only through IDE/MCP editing actions, avoiding mere output of code in chat.