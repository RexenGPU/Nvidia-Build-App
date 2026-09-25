# NVIDIA Build App

> **UNOFFICIAL FAN PROJECT** — This is a community-made fan project. It is **not** affiliated with, endorsed by, sponsored by, or officially connected to NVIDIA Corporation. "NVIDIA", the NVIDIA logo, NVIDIA NIM and all related names and marks are trademarks of NVIDIA Corporation. All trademarks belong to their respective owners.

A ChatGPT-style desktop chat app for Windows, powered by the NVIDIA NIM API ([build.nvidia.com](https://build.nvidia.com)).

![Made with C# and the NVIDIA NIM API](https://img.shields.io/badge/API-NVIDIA%20NIM-76b900)

## Features

- ChatGPT-like interface — dark NVIDIA-style theme (WebView2)
- Full markdown rendering: headings, lists, tables, quotes, **code blocks with syntax highlighting + copy button**
- Token-by-token streaming with live, collapsible AI reasoning (works with "thinking" models)
- Latest models **auto-fetched from the API** (Nemotron 3, DeepSeek, GLM-5.3, Kimi, Gemma, Mistral…) — the list is always up to date
- `/model` picker with keyboard navigation and filter, like Opencode / Claude Code
- **MCP servers** (Model Context Protocol): give the AI tools — local commands (stdio) or remote endpoints (Streamable HTTP)
- 6 languages: English (default), Fran&ccedil;ais, Espa&ntilde;ol, Deutsch, Italiano, Portugu&ecirc;s — with a first-launch picker
- Conversation history with search, pin, rename, clear and **export to Markdown**
- **Vision**: paste, drop or attach images — analyzed by vision models (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni…), marked with a VISION badge in the picker
- **Smart auto-title**: the model names each conversation itself after the first exchange
- Edit a message and regenerate, copy or delete any message, regenerate any answer
- Settings: API key, temperature, max tokens, system prompt
- Single self-contained `.exe` (about 50 MB) — no .NET runtime required

## Getting started

1. Download `NvidiaBuildApp.exe` from the [Releases](../../releases) page
2. Get a **free API key** at <https://build.nvidia.com> (NVIDIA account required)
3. Launch the app, pick your language, paste the key in **Settings** — done

Requirements: Windows 10/11 with the [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2) (preinstalled on most up-to-date systems).

## Slash commands

| Command | Action |
|---|---|
| `/model` | Pick a model (arrow keys + Enter) |
| `/key` `/settings` | Open settings / API key |
| `/export` | Export the conversation as Markdown |
| `/clear` | Clear the messages of the conversation |
| `/help` | Show help |

## MCP quick start

In **Settings &rarr; MCP servers**, add a local server, for example:

```
npx -y @modelcontextprotocol/server-filesystem C:\Users\you\Documents
```

or any Streamable HTTP endpoint such as `http://localhost:3000/mcp`.
The AI can then call these tools during the conversation (up to 8 tool rounds per answer).

## Build from source

```bash
dotnet publish NvidiaBuildApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

Tech: C# (.NET 10), WinForms + WebView2 host, single embedded HTML/CSS/JS file for the whole UI — zero JavaScript dependencies.

## License

Code is released under the [MIT License](LICENSE).

Again: this is an **unofficial fan project** — not affiliated with NVIDIA Corporation.
