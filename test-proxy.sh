#!/usr/bin/env bash
# Test script cho Tourkit AI Proxy.
# Dùng: bash test-proxy.sh           → chạy tất cả
#       bash test-proxy.sh 3         → chạy mỗi case 3
#       bash test-proxy.sh parallel  → chạy case song song

BASE="${BASE:-http://localhost:5080}"
JQ="${JQ:-cat}"   # set JQ=jq nếu có cài jq

run() {
  local name="$1" body="$2"
  echo "=== $name ==="
  curl -s -X POST "$BASE/api/ai/complete" \
    -H "Content-Type: application/json" \
    -w "\n[Total: %{time_total}s]\n" \
    --data-binary "$body" | $JQ
  echo
}

case "${1:-all}" in
  health|0|all)
    echo "=== Healthz ==="
    curl -s "$BASE/healthz"; echo
    echo "=== Models ==="
    curl -s "$BASE/api/ai/models"; echo; echo
    ;;
esac

case "${1:-all}" in
  1|ping|all)
    run "1. Ping DeepSeek Pro" '{
      "prompt": "Trả lời ngắn OK bằng tiếng Việt.",
      "model": "deepseek-v4-pro",
      "maxTokens": 100
    }'
    ;;
esac

case "${1:-all}" in
  2|meta|all)
    run "2. JSON meta tour (Step A frontend)" '{
      "prompt": "Tour Hà Nội - Sa Pa, 3N2D, focus: tổng hợp. Trả JSON thuần: {\"name\":\"TÊN TOUR\",\"tag\":\"TAGLINE\",\"titles\":[\"Ngày 1\",\"Ngày 2\",\"Ngày 3\"]}",
      "model": "deepseek-v4-pro",
      "maxTokens": 2048
    }'
    ;;
esac

case "${1:-all}" in
  3|kimi|all)
    run "3. Kimi (test parser fix)" '{
      "prompt": "Lặp lại chính xác: {\"x\":1}",
      "model": "kimi-k2.6",
      "maxTokens": 200
    }'
    ;;
esac

case "${1:-all}" in
  4|minimax|all)
    run "4. MiniMax (Anthropic format path)" '{
      "prompt": "Chào bạn",
      "model": "minimax-m2.5",
      "maxTokens": 100
    }'
    ;;
esac

case "${1:-all}" in
  parallel)
    echo "=== Song song 3 ngày ==="
    time (
      curl -s -X POST "$BASE/api/ai/complete" -H "Content-Type: application/json" \
        --data-binary '{"prompt":"Ngày 1 Sa Pa, JSON: {\"a\":[{\"h\":\"08:00\",\"n\":\"Xe đón\",\"c\":6000000}]}","model":"deepseek-v4-pro","maxTokens":1500}' > /tmp/d1.json &
      curl -s -X POST "$BASE/api/ai/complete" -H "Content-Type: application/json" \
        --data-binary '{"prompt":"Ngày 2 Sa Pa, JSON: {\"a\":[{\"h\":\"08:00\",\"n\":\"Fansipan\",\"c\":2000000}]}","model":"deepseek-v4-pro","maxTokens":1500}' > /tmp/d2.json &
      curl -s -X POST "$BASE/api/ai/complete" -H "Content-Type: application/json" \
        --data-binary '{"prompt":"Ngày 3 Sa Pa, JSON: {\"a\":[{\"h\":\"08:00\",\"n\":\"Về Hà Nội\",\"c\":6000000}]}","model":"deepseek-v4-pro","maxTokens":1500}' > /tmp/d3.json &
      wait
    )
    for f in /tmp/d{1,2,3}.json; do echo "--- $f ---"; head -c 250 "$f"; echo; done
    ;;
esac

case "${1:-all}" in
  usage|all)
    echo "=== Usage ==="
    curl -s "$BASE/api/ai/usage"; echo
    ;;
esac
