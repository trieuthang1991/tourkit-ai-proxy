# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ASP.NET Core 8 (Minimal API, single-file `Program.cs`) that proxies OpenCode Go AI calls for the Tourkit frontend. The frontend (React via UMD + Babel standalone, no build step) lives in `wwwroot/` and is served by the same process — there is no separate frontend build pipeline.

## Commands

```bash
# Run locally (binds http://localhost:5080 per Properties/launchSettings.json)
dotnet run

# Build / publish
dotnet build
dotnet publish -c Release -o out

# Docker (exposes :8080 inside container)
docker build -t tourkit-ai-proxy .
docker run -p 5080:8080 -e OPENCODE_API_KEY="sk-..." tourkit-ai-proxy
```

The upstream API key is read from `cfg["OPENCODE_API_KEY"]` first (appsettings.json) then the `OPENCODE_API_KEY` env var. There is no test project.

## Architecture

**Single-file backend (`Program.cs`)** — everything (CORS, HttpClient registration, routes, DTOs, `UsageTracker`) is in one file. The whole proxy is the `POST /api/ai/complete` handler plus three trivial GETs (`/healthz`, `/api/ai/models`, `/api/ai/usage`).

**Dual upstream protocol switch.** OpenCode Go exposes two endpoints with different wire formats. `RouteModel(model)` returns `(path, fmt)`:
- `minimax-m2.5` / `minimax-m2.7` → `zen/go/v1/messages`, Anthropic format (requires `anthropic-version: 2023-06-01` header; response is `content[].text` + `usage.{input_tokens,output_tokens}`).
- Everything else → `zen/go/v1/chat/completions`, OpenAI format (response is `choices[].message.content`, with fallbacks to `reasoning_content` for DeepSeek-style reasoning models, then `delta.content`, then `text`; usage is `prompt_tokens` / `completion_tokens`).

When adding a new model, update both `RouteModel` (if it needs the Anthropic path) and the `/api/ai/models` list. The frontend's parallel routing table is in `wwwroot/ai-provider.jsx` (`OPENCODE_GO_ENDPOINTS`) — keep them in sync.

**Request shape contract with the frontend.** The proxy accepts `{prompt, model?, maxTokens?, temperature?}` — a flat shape, NOT OpenAI's `messages[]`. `wwwroot/ai-provider.jsx::callOpenCodeGo` sends this shape when a `proxyUrl` is configured, and sends the native OpenAI/Anthropic shape only when calling OpenCode directly. Don't change the proxy DTO without updating that file.

**Frontend served from `wwwroot/`.** `UseStaticFiles` is configured with `ServeUnknownFileTypes = true` and `DefaultContentType = "text/plain"` specifically so `.jsx` and Babel-transformed files load without a registered MIME type. `index.html` pulls React 18 UMD + `@babel/standalone` from unpkg and runs `<script type="text/babel">` in-browser — there is no bundler, no npm, no node_modules. Edits to `.jsx` files take effect on browser reload.

**Usage tracking is in-memory only.** `UsageTracker` is a singleton with a lock; counters reset on process restart. Cost estimate in `Snapshot()` is hardcoded to DeepSeek V4 Pro retail pricing ($0.27/$1.10 per Mtok) regardless of which model was called.

**CORS is wide open in dev.** The `tourkit` policy lists allowed origins but then calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production (per README checklist).

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains a real-looking `OPENCODE_API_KEY`. Treat it as a secret: don't echo it, and prefer env vars for any production-bound change.
