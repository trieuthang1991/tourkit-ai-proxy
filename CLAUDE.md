# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ASP.NET Core 8 Minimal API that proxies multiple AI providers (OpenCode Go, 9routes) for the Tourkit frontend. Backend is organized by feature folders. Frontend (React via UMD + Babel standalone, no build step) lives in `wwwroot/` and is served by the same process — there is no separate frontend build pipeline.

## Commands

```bash
# Setup lần đầu: copy template + điền key thật (file appsettings.json đang gitignore)
cp appsettings.example.json appsettings.json
# Sửa appsettings.json: thay REPLACE_WITH_OPENCODE_KEY + REPLACE_WITH_9ROUTES_KEY

# Run locally (binds http://localhost:5080 per Properties/launchSettings.json)
dotnet run --project TourkitAiProxy.csproj

# Build / publish
dotnet build TourkitAiProxy.csproj
dotnet publish TourkitAiProxy.csproj -c Release -o out

# Docker (exposes :8080 inside container)
docker build -t tourkit-ai-proxy .
docker run -p 5080:8080 -e Providers__OpenCode__ApiKey="sk-..." tourkit-ai-proxy
```

There is no test project. `appsettings.json` ở `.gitignore` (chứa API keys); commit `appsettings.example.json` làm template.

## Backend layout (folder-by-feature)

```
Program.cs                                 ← thin bootstrap: DI + UseX + MapX
Configuration/
  CorsSetup.cs                             ← AddTourkitCors() extension
  StaticFilesSetup.cs                      ← UseTourkitStaticFiles() — serves wwwroot
Models/
  Dtos.cs                                  ← CompleteRequest (flat shape, see below)
  ModelRegistry.cs                         ← (legacy, used by older endpoint helper code)
Services/
  UpstreamParser.cs                        ← Parse Anthropic + OpenAI shapes
  UsageTracker.cs                          ← in-memory singleton, lock-based
  OpenCodeClient.cs                        ← shared upstream helpers (DefaultSystem const)
  Providers/
    IAiProvider.cs                         ← interface: Id, Label, Models, Complete, Stream
    ProviderRegistry.cs                    ← resolve by id, default from Providers:Default
    OpenCodeProvider.cs                    ← OpenCode Go (dual-protocol Anthropic + OpenAI)
    NineRoutesProvider.cs                  ← 9routes (OpenAI-compat local router)
Endpoints/
  SystemEndpoints.cs                       ← GET /healthz
  AiEndpoints.cs                           ← All /api/v1/* AI routes + /api/ai/* legacy aliases
```

**Adding a new provider** (e.g. OpenAI direct, Anthropic direct, Ollama local):
1. Implement `IAiProvider` in `Services/Providers/MyProvider.cs`.
2. `builder.Services.AddSingleton<IAiProvider, MyProvider>();` in `Program.cs`.
3. Read API key from `Providers:MyProvider:ApiKey` in `appsettings.json` (or env var). Never echo keys.
4. `/api/v1/providers` auto-includes the new entry — no frontend table edit needed.

## API surface (versioned + RESTful)

| Method | Path                              | Notes                                                |
|--------|-----------------------------------|------------------------------------------------------|
| GET    | `/healthz`                        | k8s-style liveness probe                             |
| GET    | `/api/v1/providers`               | list providers + their models — single source of truth |
| GET    | `/api/v1/models`                  | flat models list across all providers                |
| GET    | `/api/v1/usage`                   | UsageTracker snapshot                                |
| POST   | `/api/v1/completions`             | buffered completion                                  |
| POST   | `/api/v1/completions/stream`      | SSE stream                                           |

**Legacy aliases** (`POST /api/ai/complete`, `POST /api/ai/stream`, `GET /api/ai/models`, `GET /api/ai/usage`) point to the same handlers — keep until all clients migrate.

**Request shape** (`CompleteRequest` — flat, NOT OpenAI `messages[]`):
```json
{ "prompt": "...", "provider": "opencode-go", "model": "deepseek-v4-flash",
  "maxTokens": 8192, "temperature": 0.3, "system": "optional override" }
```
- `provider` blank → falls back to `Providers:Default` in config, then first registered.
- `system` blank → backend injects anti-reasoning prompt (see `OpenCodeClient.DefaultSystem`).
- `temperature` default `0.3` (tuned for JSON/structured output).

**Response shape (`/completions`):**
```json
{ "text": "...", "provider": "opencode-go", "model": "deepseek-v4-flash",
  "latencyMs": 1234, "inputTokens": 100, "outputTokens": 50,
  "finishReason": "stop", "attempts": 1, "warning": null, "rawUpstream": null }
```

**SSE shape (`/completions/stream`)**: a series of `data: {"delta":"..."}` events followed by terminal `data: {"done":true, text, provider, model, latencyMs, inputTokens, outputTokens, finishReason}`; on error the server emits `data: {"error":"...", status?, body?}` then `data: {"done":true}` — client must treat `error` as terminal.

## Provider details

**OpenCode Go** uses two upstream paths depending on model id:
- `minimax-m2.5` / `minimax-m2.7` → `zen/go/v1/messages`, Anthropic format. Requires BOTH `anthropic-version: 2023-06-01` AND `x-api-key` headers (bearer-only is rejected). Stream events: `message_start` / `content_block_delta` / `message_delta`.
- Everything else → `zen/go/v1/chat/completions`, OpenAI format. Streaming uses `stream_options.include_usage=true` for token counts on the final chunk. Response content falls back to `reasoning_content` / `reasoning` for DeepSeek-style models. `stop_reason: max_tokens` is normalized to `finishReason: "length"` so both paths surface OpenAI-style values.

**Retry policy in `OpenCodeProvider.CompleteAsync` (non-streaming only):**
1. *Transient retries* (up to 2): on network exception, 408, 429, or 5xx, exponential backoff (500ms × 2ⁿ on exception, 1000ms × 2ⁿ on HTTP status).
2. *Budget bump* (up to 1): if upstream returns empty `text` AND `finishReason == "length"`, double `maxTokens` (cap 16384) and retry — catches reasoning models that spend the entire budget on hidden thinking. If still empty, returns `{text: "", warning, rawUpstream}` for client-side debugging.

Streaming has NO retry.

**9routes** is an OpenAI-compatible local router (default `http://localhost:20128/v1`). Quirk: non-stream calls sometimes return SSE-formatted body — `NineRoutesProvider.ParseResponse` detects `data:` prefix and walks chunks before falling back to plain JSON.

## Frontend layout

```
wwwroot/
  index.html                                ← controls load order; <script src="..."> imperative
  styles.css
  lib/
    data.js                                 ← demo data + fmtVND
    icons.jsx                               ← Icon component
  core/
    router.jsx                              ← hash router (Router, Route, Link, navigate)
    storage.js                              ← TourCache + RequestHistory + tour stats
    parsers.js                              ← parseLooseJSON + parseTourText
    ai-provider.jsx                         ← thin client → /api/v1/completions; AISettingsDialog
  components/
    dialogs.jsx                             ← ConfirmDialog, ShareDialog, AIAssistantPanel
    tweaks-panel.jsx                        ← editorial Tweaks UI
  steps/
    step1.jsx … step4.jsx                   ← sub-views inside the wizard page
  pages/
    wizard.jsx                              ← 4-step wizard (handleGenerate orchestration here)
    quotes.jsx                              ← list of cached tours — example of a 2nd page
  app.jsx                                   ← App shell: header + nav + <Router> + global state
```

**Adding a new page:**
1. `pages/<name>.jsx`: `function MyPage({ pushToast }) {...} window.MyPage = MyPage;`
2. `index.html`: add `<script type="text/babel" src="pages/<name>.jsx"></script>` after existing pages.
3. `app.jsx`: add `<Route path="/<name>" render={() => <window.MyPage pushToast={pushToast} />} />` inside `<Router>`.
4. `app.jsx`: add `<Link to="/<name>">Tên</Link>` in the nav.

No bundler, no npm install. `<script type="text/babel">` is transformed in-browser by `@babel/standalone`.

## Cross-cutting

**Frontend reaches AI via `window.claude.complete` or `window.tourkit.ai.complete`/`completeStream`.** `core/ai-provider.jsx` shims `window.claude.complete` to delegate to `window.tourkit.ai`, which POSTs to `/api/v1/completions`. **API keys live server-side in `appsettings.json` / env vars — NEVER in client localStorage or JS.** `localStorage["tourkit_ai_config"]` only holds `{provider, model, _v}`. Bump `CONFIG_VERSION` in `ai-provider.jsx` when changing the shape.

**Static files.** `UseStaticFiles` has `ServeUnknownFileTypes = true` + `DefaultContentType = "text/plain"` so `.jsx` loads without a registered MIME type. `.jsx`/`.js`/`.css`/`.html` are served with `Cache-Control: no-cache` so edits show on a plain reload.

**Usage tracking is in-memory only.** `UsageTracker` is a singleton with a lock; counters reset on process restart. Cost estimate in `Snapshot()` is hardcoded to DeepSeek V4 Pro retail pricing ($0.27/$1.10 per Mtok) regardless of which model was called. The streaming endpoint only calls `Track` when `outTok > 0`. Usage is keyed by `"{providerId}:{model}"`.

**CORS is wide open in dev.** `CorsSetup.cs` lists allowed origins but calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production.

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains real-looking API keys. Treat them as secrets: don't echo them, and prefer env vars (e.g. `Providers__OpenCode__ApiKey`, `OPENCODE_API_KEY`, `NINE_ROUTES_API_KEY`) for any production-bound change.
- Frontend exposes singletons via `window.tourkit*` namespaces (`tourkit.ai`, `tourkitStorage`, `tourkitParsers`, `tourkitRouter`, `tourkitHistory`).
