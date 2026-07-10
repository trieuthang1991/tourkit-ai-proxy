# Sổ tay vận hành — Vbee TTS Gateway (Ubuntu)

Tài liệu để **bảo trì / thay đổi** gateway sau này. Ai không rành cứ làm theo từng lệnh.

---

## 0. Nó là gì / vì sao có

Server proxy chính chạy **Windows Server 2012 R2** (Schannel cũ) → **không** bắt tay TLS được với
`api.vbee.vn`. Con Ubuntu này làm **trung gian (relay)**: Windows chỉ gửi `{text}`, gateway lo gọi
Vbee (submit → poll → tải mp3) rồi trả về.

```
Windows proxy ──HTTPS TLS1.2──► nginx (Ubuntu) ──► Node relay :8090 ──TLS1.3──► api.vbee.vn
```

Domain: **https://vbee-gw.tourkit.vn**  ·  IP server: **103.154.63.14**

---

## 1. File nằm ở đâu

| File trên Ubuntu | Vai trò |
|---|---|
| `/opt/vbee-gateway/server.js` | Code relay (Node). Logic gọi Vbee. |
| `/opt/vbee-gateway/.env` | **Cấu hình + secret**: API key, Vbee AppId/Token, giọng, tốc độ… |
| `/etc/systemd/system/vbee-gateway.service` | Dịch vụ chạy nền (auto-restart, tự chạy khi reboot). |
| `/etc/nginx/sites-available/vbee-gateway` | Cấu hình nginx (HTTPS, cipher, proxy). |
| `/etc/letsencrypt/live/vbee-gw.tourkit.vn/` | Chứng chỉ HTTPS (cert). |

**Bản gốc trong git** (máy Windows dev): `tourkit-ai-proxy/gateway/`. Sửa ở đây rồi `scp` lên server.

---

## 2. Lệnh vận hành hằng ngày (chạy trên Ubuntu)

```bash
# Xem trạng thái dịch vụ
sudo systemctl status vbee-gateway --no-pager

# Xem log gần nhất (theo dõi realtime: thêm -f)
sudo journalctl -u vbee-gateway -n 50 --no-pager

# Khởi động lại (sau khi sửa .env hoặc server.js)
sudo systemctl restart vbee-gateway

# Test nội bộ (Node còn sống không)
curl -s http://127.0.0.1:8090/healthz; echo        # → ok

# Test HTTPS từ ngoài
curl -s https://vbee-gw.tourkit.vn/healthz; echo    # → ok

# Test sinh audio (thay YOUR_KEY = GATEWAY_API_KEY trong .env)
curl -s -X POST https://vbee-gw.tourkit.vn/vbee/tts \
  -H "X-Api-Key: YOUR_KEY" -H "Content-Type: application/json" \
  -d '{"text":"Xin chào."}' -o /tmp/test.mp3 && ls -la /tmp/test.mp3
```

---

## 3. Cách THAY ĐỔI từng thứ

### A. Đổi giọng / tốc độ / token Vbee  → sửa `.env`
```bash
sudo nano /opt/vbee-gateway/.env
# sửa dòng cần đổi, ví dụ:
#   VBEE_VOICE=hn_female_ngochuyen_full_48k-fhg   ← đổi giọng
#   VBEE_SPEED=1.0                                ← tốc độ đọc (0.8 chậm, 1.2 nhanh)
#   VBEE_TOKEN=...                                ← khi token Vbee hết hạn/đổi
# Lưu: Ctrl+O, Enter, Ctrl+X
sudo systemctl restart vbee-gateway               # BẮT BUỘC restart để áp dụng
```
> Danh sách giọng Vbee: xem tại studio.vbee.vn. Chỉ đổi ở đây, **không** cần đụng Windows.

### B. Đổi API key (khóa Windows↔gateway)  → sửa 2 nơi
```bash
# 1) Sinh key mới trên Ubuntu:
openssl rand -hex 32                               # copy chuỗi in ra
# 2) Dán vào .env:
sudo nano /opt/vbee-gateway/.env                   # GATEWAY_API_KEY=<key mới>
sudo systemctl restart vbee-gateway
```
Rồi trên **Windows** sửa `appsettings.json` → `Speech:Vbee:GatewayKey` = key mới → **restart app Windows**.
(Hai nơi phải TRÙNG nhau, không thì gateway trả 401.)

### C. Cập nhật logic relay  → sửa `server.js`
Sửa file gốc trong git (`tourkit-ai-proxy/gateway/server.js`) rồi copy lên bằng **PowerShell (máy Windows)**:
```powershell
scp "D:\MiGroup\tourkitapp\tourkit-ai-proxy\gateway\server.js" root@103.154.63.14:/opt/vbee-gateway/server.js
```
Quay lại **Ubuntu** — kiểm cú pháp TRƯỚC khi restart (tránh chết dịch vụ):
```bash
node --check /opt/vbee-gateway/server.js && echo ">>> OK <<<"
sudo systemctl restart vbee-gateway
sudo systemctl status vbee-gateway --no-pager
```
> ⚠️ **Đừng dán server.js trực tiếp vào terminal** — file có dòng dài, dán qua SSH hay rớt ký tự → sai cú pháp. Luôn dùng `scp`.

### D. Sửa nginx (đổi domain / cipher / timeout)  → sửa file nginx
Sửa gốc `gateway/nginx-vbee-gateway.conf` rồi `scp` (PowerShell Windows):
```powershell
scp "D:\MiGroup\tourkitapp\tourkit-ai-proxy\gateway\nginx-vbee-gateway.conf" root@103.154.63.14:/etc/nginx/sites-available/vbee-gateway
```
Ubuntu — **luôn test trước khi reload** (test lỗi mà reload là sập web):
```bash
sudo nginx -t                                      # phải: test is successful
sudo systemctl reload nginx                         # chỉ reload khi test OK
```
Sửa nhanh 1 dòng ngay trên server (không cần scp), ví dụ tăng timeout:
```bash
sudo nano /etc/nginx/sites-available/vbee-gateway
sudo nginx -t && sudo systemctl reload nginx
```

### E. Gia hạn chứng chỉ (cert)
Certbot **tự gia hạn** (có timer). Kiểm + thử:
```bash
sudo certbot certificates                          # xem ngày hết hạn
sudo certbot renew --dry-run                        # thử gia hạn (không thật)
```
Gia hạn tay nếu cần (cert này là **RSA** — bắt buộc giữ RSA cho 2012 R2):
```bash
sudo systemctl stop nginx
sudo certbot certonly --standalone -d vbee-gw.tourkit.vn --key-type rsa --force-renewal
sudo systemctl start nginx
```
> ⚠️ **Luôn dùng `--key-type rsa`.** Nếu để mặc định (ECDSA) → 2012 R2 sẽ KHÔNG bắt tay được → giọng đọc hỏng lại.

---

## 4. Xử lý sự cố nhanh

| Triệu chứng | Kiểm tra | Cách sửa |
|---|---|---|
| Windows kêu `TTS edge/openai` (không phải vbee) | log Windows dòng `Vbee gateway lỗi ...` | xem mục tương ứng dưới |
| `... 401 unauthorized` | key 2 nơi khác nhau | mục **3B** — cho `GatewayKey` (Windows) = `GATEWAY_API_KEY` (.env) |
| `... Could not create SSL/TLS` từ Windows | cert bị ECDSA / cipher | mục **3E** (cấp lại RSA) |
| `curl .../healthz` không ra `ok` | `systemctl status vbee-gateway` | nếu failed: `journalctl -u vbee-gateway` xem lỗi (thường thiếu env) |
| Node chết ngay khi start | `journalctl -u vbee-gateway -n 20` | thiếu `GATEWAY_API_KEY`/`VBEE_APP_ID`/`VBEE_TOKEN` trong .env |
| `nginx -t` báo lỗi | đọc dòng lỗi | thường sai đường dẫn cert / directive lạ → sửa file nginx |
| Vbee gen chậm/timeout | `journalctl` dòng `poll quá hạn` | tăng `VBEE_POLL_TIMEOUT` trong .env (mục 3A) + `proxy_read_timeout` nginx (3D) |

**Xem log lỗi cụ thể của 1 request TTS:**
```bash
sudo journalctl -u vbee-gateway -n 100 --no-pager | grep -i "vbee/tts"
```

---

## 5. Checklist khi setup lại từ đầu (nếu đổi server)

1. Tạo A record (mây xám) domain → IP server.
2. `apt install nodejs nginx certbot python3-certbot-nginx` (Node ≥ 18).
3. Mở firewall 80+443 (`ufw allow 'Nginx Full'` + firewall nhà cung cấp VPS).
4. scp `server.js` → `/opt/vbee-gateway/` ; tạo `.env` (điền key + Vbee creds) ; tạo user `vbeegw`.
5. scp `vbee-gateway.service` → `/etc/systemd/system/` ; `systemctl enable --now vbee-gateway`.
6. `certbot certonly --standalone -d <domain> --key-type rsa` (nhớ **--key-type rsa**).
7. scp `nginx-vbee-gateway.conf` → `/etc/nginx/sites-available/vbee-gateway` (đổi domain trong file) ;
   `ln -s` sang sites-enabled ; `nginx -t` ; `reload`.
8. Test từ chính Windows 2012 R2: `Invoke-WebRequest https://<domain>/healthz` → `200/ok`.
9. Windows `appsettings.json`: `Speech:Vbee:GatewayUrl` + `GatewayKey` → restart app.
