# Vbee TTS Gateway (Ubuntu + nginx)

Relay nhỏ giúp server proxy chính (Windows Server 2012 R2, Schannel cũ **không** gọi được
`api.vbee.vn` — thiếu TLS 1.3/x25519) vẫn dùng được giọng đọc Vbee. Con Ubuntu này gọi Vbee
bình thường và làm trung gian.

```
Windows proxy ──HTTPS TLS1.2──► nginx (Ubuntu) ──► Node relay :8090 ──TLS1.3──► api.vbee.vn
   gửi {text}      cipher tương thích 2012 R2       giữ AppId/Token        submit→poll→tải mp3
```

Chỉ cần **Node 18+** (không cần cài package nào — zero dependency).

---

## 1. Chuẩn bị

- 1 domain trỏ về IP con Ubuntu, ví dụ `vbee-gw.tourkit.vn` (A record).
- Mở firewall 80 + 443.

```bash
sudo apt update
sudo apt install -y nodejs nginx certbot python3-certbot-nginx
node -v   # phải >= 18
```

## 2. Cài relay

```bash
sudo mkdir -p /opt/vbee-gateway
sudo cp server.js /opt/vbee-gateway/
sudo cp .env.example /opt/vbee-gateway/.env
sudo adduser --system --no-create-home vbeegw

# Sinh API key ngẫu nhiên (LƯU LẠI — Windows sẽ cần):
openssl rand -hex 32

sudo nano /opt/vbee-gateway/.env     # điền GATEWAY_API_KEY (key vừa sinh) + VBEE_APP_ID + VBEE_TOKEN
sudo chown vbeegw /opt/vbee-gateway/.env
sudo chmod 600 /opt/vbee-gateway/.env

sudo cp vbee-gateway.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now vbee-gateway
sudo systemctl status vbee-gateway          # phải Active (running)
curl -s http://127.0.0.1:8090/healthz       # → ok
```

## 3. nginx + HTTPS

```bash
# ĐỔI vbee-gw.tourkit.vn trong file thành domain thật TRƯỚC khi copy:
sudo cp nginx-vbee-gateway.conf /etc/nginx/sites-available/vbee-gateway
sudo ln -s /etc/nginx/sites-available/vbee-gateway /etc/nginx/sites-enabled/

# Lấy cert Let's Encrypt (certbot tự chèn ssl_certificate vào file):
sudo certbot --nginx -d vbee-gw.tourkit.vn

sudo nginx -t && sudo systemctl reload nginx
```

## 4. Test từ ngoài

```bash
# Healthz
curl -s https://vbee-gw.tourkit.vn/healthz     # → ok

# Thử sinh audio (thay YOUR_KEY):
curl -s -X POST https://vbee-gw.tourkit.vn/vbee/tts \
  -H "X-Api-Key: YOUR_KEY" -H "Content-Type: application/json" \
  -d '{"text":"Xin chào, đây là giọng Vbee."}' -o test.mp3
ls -la test.mp3     # có file mp3 vài chục KB = OK
```

**Quan trọng — test từ CHÍNH Windows Server 2012 R2** (đảm bảo bắt tay TLS được):
```powershell
Invoke-WebRequest https://vbee-gw.tourkit.vn/healthz -UseBasicParsing
# Nếu ra 'ok' → 2012 R2 kết nối gateway OK (cipher đã tương thích). Nếu vẫn lỗi SSL,
# báo lại — có thể cần nới thêm cipher trong nginx-vbee-gateway.conf.
```

## 5. Trỏ Windows proxy sang gateway

Trong `appsettings.json` của **TourkitAiProxy** (Windows), thêm vào `Speech:Vbee`:

```jsonc
"Speech": {
  "Tts": { "Provider": "vbee", "Fallback": true },
  "Vbee": {
    "Enabled": true,
    "GatewayUrl": "https://vbee-gw.tourkit.vn",
    "GatewayKey": "YOUR_KEY",          // đúng GATEWAY_API_KEY ở .env gateway
    "Voice": "hn_female_ngochuyen_full_48k-fhg"
    // KHÔNG cần AppId/Token trên Windows nữa — gateway giữ. (Để lại cũng không sao.)
  }
}
```

Restart app Windows → hỏi 1 câu trên `/jarvis` → header hiện **`TTS vbee`**, đọc tiếng Việt chuẩn,
hết cảnh chờ ~59s.

---

## Ghi chú

- **Bảo mật:** gateway chỉ mở endpoint `/vbee/tts` + `/healthz`, yêu cầu `X-Api-Key`. Vbee
  AppId/Token chỉ nằm trên Ubuntu. Nên giới hạn thêm bằng firewall (chỉ cho IP Windows gọi 443)
  nếu muốn chặt hơn.
- **Cipher 2012 R2:** file nginx đã bật TLS 1.2 + ECDHE-RSA P-256/384 + RSA-GCM/CBC SHA256 —
  bộ mà Schannel 2012 R2 hỗ trợ. Nếu máy đã hardening tắt bớt, `Get-TlsCipherSuite` trên Windows
  để xem còn cipher nào chung.
- **Cache:** app Windows cache câu lặp (chào/filler) in-mem → không gọi gateway lại.
- **Mở rộng:** muốn relay thêm edge-tts sau này → thêm 1 route nữa vào `server.js` (proxy WebSocket).
