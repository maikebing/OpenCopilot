# OpenCopilot

A GitHub Copilot-like AI coding assistant for **Visual Studio 2022, 2025, and 2026**, supporting multiple LLM providers including cloud services and local offline models.

## Features

- 🤖 **Inline Code Completion** — AI-powered suggestions as you type, integrated with Visual Studio's IntelliSense
- 💬 **AI Chat Window** — Conversational coding assistant panel (like GitHub Copilot Chat)
- 🔧 **Code Commands** — Explain Code, Generate Code, Fix Code (right-click or menu)
- 🌐 **Multiple LLM Providers** — Switch between cloud and local AI models
- ⚙️ **Configurable Settings** — Per-provider API keys, models, and endpoints in Tools > Options

## Supported LLM Providers

| Provider | Type | Notes |
|---|---|---|
| **OpenAI** | Cloud | GPT-4o, GPT-4o-mini, GPT-4-turbo, etc. |
| **DeepSeek** | Cloud | deepseek-chat, deepseek-coder, deepseek-reasoner |
| **Doubao (豆包)** | Cloud | ByteDance / Volcengine — doubao-pro-32k, etc. |
| **Ollama** | Local | Any model installed locally (codellama, llama3, mistral, …) |
| **Docker Desktop AI** | Local | Models served via Docker Desktop's built-in AI engine |

## Project Structure

```
OpenCopilot.sln
src/
  OpenCopilot.Core/          # netstandard2.1 — providers, services, options model
    Providers/               # ILlmProvider + OpenAI, DeepSeek, Doubao, Ollama, DockerDesktopAI
    Services/                # LlmService (provider orchestrator + request builders)
    Options/                 # OpenCopilotOptions (settings model)
  OpenCopilot/               # net472 VSIX — Visual Studio 2022 / 2025 / 2026 extension
    OpenCopilotPackage.cs    # AsyncPackage entry point
    Completion/              # IAsyncCompletionSource for inline AI completions
    Commands/                # Explain / Generate / Fix code commands
    Options/                 # Tools > Options dialog page
    ToolWindows/             # WPF chat panel
  OpenCopilot.Tests/         # net8.0 — xUnit tests (25 tests)
```

## Getting Started

### Prerequisites
- Visual Studio 2022 (17.0+), 2025 (18.0+), or 2026 (19.0+)
- .NET Framework 4.7.2 (included with VS2022 and later)
- Visual Studio SDK (for building from source)

### Building
```bash
dotnet build OpenCopilot.sln
```

### Running Tests
```bash
dotnet test src/OpenCopilot.Tests/OpenCopilot.Tests.csproj
```

### Installation
1. Build the solution in Release mode
2. The VSIX file is generated at `src/OpenCopilot/bin/Release/OpenCopilot.vsix`
3. Double-click the `.vsix` file to install into Visual Studio

## Configuration

Open **Tools > Options > OpenCopilot** to configure:

- **Active Provider** — Select which LLM to use (OpenAI, DeepSeek, Doubao, Ollama, Docker Desktop AI)
- **OpenAI** — API Key, Model, Base URL (supports any OpenAI-compatible API)
- **DeepSeek** — API Key, Model
- **Doubao (豆包)** — Volcengine API Key, Model
- **Ollama (Local)** — Base URL (default: `http://localhost:11434`), Model
- **Docker Desktop AI (Local)** — Base URL, Model
- **Temperature** — Creativity level (0.0 = deterministic, 1.0 = creative; 0.2 recommended for code)
- **Max Tokens** — Maximum response length
- **Enable Inline Completion** — Toggle AI completions on/off

## Usage

### Inline Completion
Just type code — OpenCopilot will automatically suggest completions via the IntelliSense dropdown.

### Chat Window
Open via **View > Other Windows > OpenCopilot Chat** (or the menu command). Ask questions about your code, request explanations, or get code generated.

### Commands
Right-click in the editor or use the **OpenCopilot** menu:
- **Explain Code** — Explain the selected code
- **Generate Code** — Generate code from a description
- **Fix Code** — Fix issues in the selected code

## Architecture

The extension uses a layered architecture:
1. **Provider Layer** (`OpenCopilot.Core/Providers/`) — Each provider implements `ILlmProvider` with `CompleteAsync` and `StreamCompleteAsync`
2. **Service Layer** (`OpenCopilot.Core/Services/LlmService`) — Manages providers, delegates calls, builds prompts
3. **VS Integration Layer** (`OpenCopilot/`) — VSIX package, MEF completion source, commands, tool window

## License

See [LICENSE](LICENSE).
