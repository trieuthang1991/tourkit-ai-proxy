# Nhà cung cấp AI và function-calling

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Provider details

**OpenCode Go** uses two upstream paths depending on model id:
- `minimax-m2.5` / `minimax-m2.7` → `zen/go/v1/messages`, Anthropic format. Requires BOTH `anthropic-version: 2023-06-01` AND `x-api-key` headers (bearer-only is rejected). Stream events: `message_start` / `content_block_delta` / `message_delta`.
- Everything else → `zen/go/v1/chat/completions`, OpenAI format. Streaming uses `stream_options.include_usage=true` for token counts on the final chunk. Response content falls back to `reasoning_content` / `reasoning` for DeepSeek-style models. `stop_reason: max_tokens` is normalized to `finishReason: "length"` so both paths surface OpenAI-style values.

**Retry policy in `OpenCodeProvider.CompleteAsync` (non-streaming only):**
1. *Transient retries* (up to 2): on network exception, 408, 429, or 5xx, exponential backoff (500ms × 2ⁿ on exception, 1000ms × 2ⁿ on HTTP status).
2. *Budget bump* (up to 1): if upstream returns empty `text` AND `finishReason == "length"`, double `maxTokens` (cap 16384) and retry — catches reasoning models that spend the entire budget on hidden thinking. If still empty, returns `{text: "", warning, rawUpstream}` for client-side debugging.

Streaming has NO retry.

**9routes** is an OpenAI-compatible local router (default `http://localhost:20128/v1`). Quirk: non-stream calls sometimes return SSE-formatted body — `NineRoutesProvider.ParseResponse` detects `data:` prefix and walks chunks before falling back to plain JSON.

## Native function-calling (Anthropic) — dual-path scoring

5 single-shot AI feature (Customer Review / Visa / Deal / Tour Builder / Mail Classify) đều có **2 path**:

| Provider hiện hành | Path chạy | Output enforce |
|--------------------|-----------|----------------|
| `anthropic` (`Providers:Default=anthropic`) | NATIVE function-calling: AI gọi terminal tool (`submit_*`) với JSON Schema enforce | Schema validate type/enum/required → 0% leak markdown/thinking |
| `opencode-go` / `nine-routes` / `openai` (default hiện tại) | JSON-prompt: AI in JSON ra text + tolerant parse + retry x1 | Legacy — phụ thuộc prompt discipline |

**Switch path:** đổi `appsettings.json` → `"Providers": { "Default": "anthropic" }` + nhập `"Anthropic": { "ApiKey": "sk-ant-..." }` (hoặc env `ANTHROPIC_API_KEY`). Trace sẽ hiện `path_dispatch: native-tool` thay vì `json-prompt`. **No breaking change** khi giữ default cũ — JSON path vẫn chạy như trước.

**Shared infrastructure (`Services/Workflow/`):**
- **`AnthropicToolsClient`** — agentic loop tổng quát cho `api.anthropic.com/v1/messages` với `tools[]`. Max 5 iter, terminal tool detection (dừng khi AI gọi `submit_*`), wall-clock 60s, tự ghi trace cho mỗi iter + tool dispatch. Trả `ToolsResult { TerminalInput, Iterations, TokensIn/Out, Latency, Warning }`. Reusable cho mọi feature single-shot HOẶC multi-step.
- **`NativeToolScorer.RunAsync<T>(systemPrompt, userPrompt, schema, terminalToolName, parser, apiKey, model, maxTokens, trace)`** — thin wrapper cho score-like service: resolve apiKey (override → `ProviderKeyStore` fallback), gọi `AnthropicToolsClient`, throw nếu terminal null, parse → `T`, ghi `AiUsageLog`. `BuildAnthropicTool(name, description, properties, required[])` helper để khỏi nhớ shape `{name, description, input_schema:{type,properties,required}}`.

**2 routing pattern:**
1. **Strategy pattern (Customer Review)** — `IReviewAgent` interface + 2 class (`NativeToolReviewAgent`, `JsonPromptReviewAgent`). Đăng ký `IEnumerable<IReviewAgent>` ở DI (NativeTool TRƯỚC, Json SAU — thứ tự quan trọng). `ReviewService` resolve agent đầu tiên `Supports(defaultProviderId)`. Áp dụng khi schema rich + có thể mở rộng (vd Mức C multi-step augmentation).
2. **In-service routing (Visa / Deal / Tour / Mail)** — `ScoreAsync` top: `if provider.Id == "anthropic" → ScoreWithNativeToolAsync; else → ScoreWithJsonPromptAsync`. Đơn giản hơn, không cần interface. Áp dụng khi schema nhỏ + ít kịch bản mở rộng.

**Tool schema convention:** `submit_<entity>_<action>` (vd `submit_visa_score`, `submit_tour_draft`). Properties với `type` + `enum` + `description`; nullable dùng `type: ["string", "null"]` (JSON Schema 2020-12, Anthropic accepts). `required[]` chỉ list field BẮT BUỘC có — optional field có thể omit hoặc null. Parser dùng chung helper case-insensitive lookup từ `ReviewPrompt.ParseElement` hoặc local `TryGet/Str/Int/StrList`.

**Tradeoffs:**
- Native: 0% format error, dùng được haiku rẻ (vd Mail Classifier), không cần retry. Phụ thuộc API có function-calling (chỉ Anthropic, sau này thêm OpenAI Responses).
- JSON: chạy mọi provider (kể cả reasoning model), nhưng ~5-10% trả format xấu → retry x1.

## Thêm một nhà cung cấp mới

**Adding a new provider** (e.g. OpenAI direct, Anthropic direct, Ollama local):
1. Implement `IAiProvider` in `TourkitAiProxy.Services/Providers/MyProvider.cs`.
2. `builder.Services.AddSingleton<IAiProvider, MyProvider>();` in `Program.cs`.
3. Read API key from `Providers:MyProvider:ApiKey` in `appsettings.json` (or env var). Never echo keys.
4. `/api/v1/providers` auto-includes the new entry — no frontend table edit needed.
