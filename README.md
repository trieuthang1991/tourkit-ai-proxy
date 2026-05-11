# Tourkit AI Proxy (ASP.NET Core 8)

Proxy nhỏ giấu API key OpenCode Go + thống nhất endpoint cho Tourkit React frontend.

## Chạy local trong 30 giây

```bash
cd tourkit-ai-proxy

# 1. Cấu hình key (chọn 1 trong 2 cách)
#    Cách A: sửa appsettings.json → OPENCODE_API_KEY
#    Cách B: dùng env var (an toàn hơn)
export OPENCODE_API_KEY="sk-..."        # macOS/Linux
$env:OPENCODE_API_KEY = "sk-..."        # PowerShell
set OPENCODE_API_KEY=sk-...             # CMD

# 2. Chạy
dotnet run
# → http://localhost:5080
```

## Test nhanh

```bash
# Healthcheck
curl http://localhost:5080/

# List models
curl http://localhost:5080/api/ai/models

# Gọi AI (DeepSeek V4 Flash — default, nhanh & rẻ)
curl -X POST http://localhost:5080/api/ai/complete \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "Liệt kê 3 điểm du lịch nổi tiếng ở Vĩnh Phúc, trả JSON: {\"places\":[...]}",
    "model": "deepseek-v4-flash",
    "maxTokens": 300
  }'

# Xem usage
curl http://localhost:5080/api/ai/usage
```

## Tích hợp vào Tourkit frontend

1. Mở `index.html` → click button **"AI: ..."** ở header
2. Provider: **OpenCode Go (DeepSeek)**
3. Backend Proxy URL: `http://localhost:5080/api/ai`
4. Bỏ trống API Key (server-side đã có)
5. Click **Test kết nối**

## Docker

```bash
docker build -t tourkit-ai-proxy .
docker run -p 5080:8080 -e OPENCODE_API_KEY="sk-..." tourkit-ai-proxy
```

## Endpoints

| Method | Path | Mô tả |
|---|---|---|
| GET | `/` | Healthcheck |
| GET | `/api/ai/models` | Danh sách model OpenCode Go |
| GET | `/api/ai/usage` | Token đã dùng + cost estimate |
| POST | `/api/ai/complete` | Body: `{prompt, model?, maxTokens?, temperature?}` → `{text, latencyMs, inputTokens, outputTokens}` |

## Production checklist

- [ ] Đặt `OPENCODE_API_KEY` qua env var (KHÔNG commit vào appsettings.json)
- [ ] Siết CORS `WithOrigins(...)` đúng domain prod, bỏ `SetIsOriginAllowed(_ => true)`
- [ ] Thêm rate limit (`AddRateLimiter`) chống abuse
- [ ] Auth từng request (JWT/cookie) để tránh proxy bị crawler dùng free
- [ ] Log usage vào DB (PostgreSQL) thay vì in-memory `UsageTracker`
- [ ] Cache aggressive: prompt giống nhau trong 1h → trả cached result
- [ ] Health check `/health` cho load balancer
