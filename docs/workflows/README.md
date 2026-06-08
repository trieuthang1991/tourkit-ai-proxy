# Workflow Runbook — AI Features

JSON runbook mô tả end-to-end mỗi tính năng AI: pipeline, API calls (HTTP method/URL/body), prompt template, model dùng, persistence, frontend page render, known issues.

Mục đích:
- Onboarding nhanh dev mới (đọc 1 file JSON là hiểu feature)
- Debug khi feature lỗi (tra theo step → biết AI call đang ở đâu)
- Audit cost (xem prompt size + model + token estimate)
- Document cho deploy (admin biết feature gọi endpoint nào, lưu DB nào)

## 4 file

| Workflow | File | Service | UI Page |
|---|---|---|---|
| Customer Review (chấm hạng A-D) | [`customer-review.json`](./customer-review.json) | `ReviewService` | `/customers` |
| Visa AI Scoring | [`visa-ai.json`](./visa-ai.json) | `VisaExtractionService` + `VisaScoringService` | `/visa` |
| Deal AI Priority | [`deal-scoring.json`](./deal-scoring.json) | `DealBatchService` + `DealScoringService` | `/deals` |
| Wizard Tour Quote | [`wizard-quote.json`](./wizard-quote.json) | `/completions` passthrough | `/wizard` |

## Cấu trúc chung mỗi file

```json
{
  "workflow": "Tên service backend",
  "displayName": "Tên hiển thị tiếng Việt",
  "purpose": "Mục đích 1-2 câu",
  "frontendPage": "/<route>",
  "triggers": [{ "ui": "Mô tả thao tác user", "calls": "endpoint chính" }],
  "endpoints": [{ "method", "path", "auth", "body", "response" }],
  "pipeline": [
    { "step": "1. ...", "service": "...", "apiCall": { ... }, "duration": "..." }
  ],
  "aiPrompt": { "system": "...", "userTemplate": "..." },
  "models": { "default": "...", "alternatives": "..." },
  "persistence": { "table|file": "...", "auditLog": "..." },
  "frontendRender": { "page": "wwwroot/...", "displayElements": [...] },
  "knownIssues": ["..."]
}
```

## Đọc nhanh

- **Phần `pipeline`** — từng step service-level, xem AI call ở đâu, gọi URL nào, tốn bao lâu.
- **Phần `aiPrompt`** — copy hệt system + user prompt template để debug khi AI ra output xấu.
- **Phần `persistence`** — biết data đi đâu (DB table nào, file JSON nào, cache TTL bao lâu).
- **Phần `knownIssues`** — gotcha + TODO.

## Cập nhật

Khi sửa workflow (đổi prompt, đổi model, thêm step), nhớ sync JSON này. Idealy là "code change → JSON update" cùng commit.

## Quan sát runtime

Workflow trace (mỗi request có `?debug=1`) tự lưu vào `data/workflow-traces.jsonl` — xem ở `/ai-usage` tab "Workflow log".
