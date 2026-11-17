# Dựng Postgres cho hộp thư chat trên máy Linux mới

Sổ tay vận hành — từ máy trắng đến lúc hộp thư nhận tin trở lại. Làm tuần tự từ trên xuống; mỗi
bước có **dấu hiệu đã xong** để biết đã chạy thật hay chỉ trông như xong.

| | |
|---|---|
| Cơ sở dữ liệu | `tourkit-chat` |
| Người dùng | `tourkit_chat` |
| Phiên bản | PostgreSQL 17 |
| Bắt buộc | `SSL Mode = Require` |

**Không phải tạo bảng nào.** Ứng dụng tự dựng toàn bộ schema lúc khởi động
(`ChatDb.InitAsync`) — bạn chỉ cần một cơ sở dữ liệu **rỗng** đúng tên và một người dùng đủ quyền.
Đừng đi tìm file SQL để nhập, không có file nào cả.

**Thứ tự có ý nghĩa.** Đây là quy trình thật, không phải danh sách gợi ý: bước 4 khai địa chỉ mạng
mà địa chỉ đó đến bước 6 mới sinh ra, nên bước 4 **cố ý để dở** và quay lại sửa một dòng sau. Thấy
vậy không phải bạn làm sai.

---

## Bước 0 — Khảo sát máy đích

Máy sẵn có thường đang gánh việc khác. Năm lệnh này quyết định cả bước 5 lẫn chuyện có nên dùng máy
này hay không.

```bash
lsb_release -a
free -h
df -h /

# đã có Postgres nào chạy chưa
sudo ss -lntp | grep 5432
pg_lsclusters 2>/dev/null || echo "chưa có cluster nào"
```

**Đọc kết quả**

| Thấy gì | Nghĩa là | Làm gì |
|---|---|---|
| `5432` đang mở | Đã có Postgres trên máy | Đừng cài đè — sẽ đẻ ra cluster thứ hai ở cổng 5433 và bạn sẽ nối nhầm. Quyết định dùng lại hay gỡ trước. |
| RAM < 2GB | Máy nhỏ | Dùng cột 2GB ở bước 5, đừng dùng số mặc định. |
| Đĩa trống < 20GB | Không đủ chỗ | Dọn trước. Đầy đĩa là nguyên nhân Postgres chết nhiều hơn mọi lý do khác cộng lại. |

⚠️ **Nếu đây là máy đang chạy việc khác quan trọng** — ví dụ cầu nối TTS — thì cân nhắc lại. Postgres
giữ chặt RAM theo `shared_buffers`, lúc cơ sở dữ liệu bận thì việc kia chậm theo. Hai việc quan
trọng chung một máy nghĩa là **một máy chết là chết cả hai**. Vẫn dùng chung thì bắt buộc làm bước 7.

---

## Bước 1 — Cài PostgreSQL 17

Lấy từ kho chính thức PGDG, không dùng bản mặc định của Ubuntu — thường cũ vài phiên bản và sẽ phải
nâng cấp lại giữa chừng.

```bash
sudo apt update && sudo apt install -y curl ca-certificates gnupg

sudo install -d /usr/share/postgresql-common/pgdg
sudo curl -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
  --fail https://www.postgresql.org/media/keys/ACCC4CF8.asc

echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] \
https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
  | sudo tee /etc/apt/sources.list.d/pgdg.list

sudo apt update && sudo apt install -y postgresql-17
```

**Dấu hiệu đã xong**

```bash
systemctl is-enabled postgresql   # → enabled
systemctl is-active  postgresql   # → active
psql --version                    # → psql (PostgreSQL) 17.x
```

Gói Debian tự bật `ssl = on` và tự khởi động cùng máy, nên hai việc đó bạn không phải làm.

---

## Bước 2 — Tạo cơ sở dữ liệu và người dùng

Tên phải khớp đúng cấu hình ứng dụng đang chờ, nếu không sẽ phải sửa chuỗi kết nối ở hai máy thay
vì một chỗ.

Nghĩ một mật khẩu mạnh **mới** — đừng dùng lại mật khẩu của máy cũ.

```bash
sudo -u postgres psql <<'SQL'
CREATE ROLE tourkit_chat LOGIN PASSWORD 'MẬT_KHẨU_MỚI';

CREATE DATABASE "tourkit-chat"
  OWNER tourkit_chat
  ENCODING 'UTF8'
  LC_COLLATE 'C.UTF-8'
  LC_CTYPE   'C.UTF-8'
  TEMPLATE   template0;

\c "tourkit-chat"
GRANT ALL ON SCHEMA public TO tourkit_chat;
SQL
```

⚠️ **Tên cơ sở dữ liệu có dấu gạch ngang** nên bắt buộc để trong nháy kép ở mọi câu lệnh. Thiếu nháy
là lỗi cú pháp, và thông báo lỗi của Postgres không nói rõ nguyên nhân.

⚠️ **Dòng `GRANT ALL ON SCHEMA public` không bỏ được.** Từ PostgreSQL 15, schema `public` không còn
cho ghi tự do. Thiếu nó thì app khởi động bình thường rồi chết ở bước dựng bảng — mà lỗi hiện ra là
`permission denied for schema public`, rất dễ đi sửa nhầm chỗ.

**Dấu hiệu đã xong**

```bash
sudo -u postgres psql -l | grep tourkit-chat
```

Thấy một dòng có `tourkit-chat`, chủ sở hữu `tourkit_chat`, mã hoá `UTF8`.

---

## Bước 3 — Xác nhận SSL đang bật

Ứng dụng nối bằng `SSL Mode = Require`. Máy chủ không có SSL thì không kết nối được, và thông báo
lỗi rất mơ hồ — kiểm ngay bây giờ rẻ hơn nhiều so với đi mò sau.

```bash
sudo -u postgres psql -c "SHOW ssl;"
```

Trả về `on` là xong. Bản Ubuntu/Debian dùng chứng chỉ tự ký sẵn có, và chuỗi kết nối đã đặt
`TrustServerCertificate = true` nên không cần chứng chỉ thật. **Đừng tắt SSL đi cho "gọn".**

Nếu trả về `off` — hiếm, nhưng có — thì mở `/etc/postgresql/17/main/postgresql.conf`, đặt
`ssl = on`, và kiểm tra hai file sau tồn tại: `/etc/ssl/certs/ssl-cert-snakeoil.pem` và
`/etc/ssl/private/ssl-cert-snakeoil.key`.

---

## Bước 4 — Mở cổng cho đúng mạng

Chỉ nghe trên máy nội bộ và mạng riêng Tailscale. **Không bao giờ nghe trên `0.0.0.0`.**

`/etc/postgresql/17/main/postgresql.conf`:

```
listen_addresses = 'localhost'   # bước 6 sẽ thêm địa chỉ Tailscale vào đây
```

Thêm vào **cuối** `/etc/postgresql/17/main/pg_hba.conf`:

```
hostssl  tourkit-chat  tourkit_chat  100.64.0.0/10  scram-sha-256
```

Dùng `hostssl` chứ không phải `host`: nó **ép** mọi kết nối phải mã hoá. Dải `100.64.0.0/10` là dải
riêng của Tailscale — không ai ngoài mạng riêng của bạn có địa chỉ trong đó.

Chưa nạp lại vội — quay lại đây sau bước 6, khi đã có địa chỉ Tailscale thật.

---

## Bước 5 — Tinh chỉnh theo RAM

Mặc định của Postgres đặt cho máy rất nhỏ. Tra cột đúng với RAM máy bạn, chép vào cuối
`postgresql.conf`.

| Tham số | 2 GB | 4 GB | 8 GB | Vì sao |
|---|---|---|---|---|
| `shared_buffers` | 256MB | 1GB | 2GB | Bộ nhớ đệm riêng của Postgres. Đừng quá 40% RAM — nhanh hơn không đáng kể mà dễ bị hệ điều hành giết khi thiếu bộ nhớ. |
| `effective_cache_size` | 1GB | 3GB | 6GB | Chỉ là ước lượng để chọn kế hoạch truy vấn, không cấp phát thật. |
| `work_mem` | 4MB | 8MB | 16MB | Nhân với số truy vấn chạy song song — đặt to là hết RAM rất nhanh. |
| `maintenance_work_mem` | 128MB | 256MB | 512MB | Dùng khi dọn dẹp và tạo chỉ mục. |
| `max_connections` | 50 | 100 | 100 | Hộp thư chat không cần nhiều — mỗi kết nối tốn RAM riêng. |
| `wal_compression` | on | on | on | Giảm đáng kể lượng ghi đĩa, gần như không tốn CPU. |

```bash
sudo systemctl restart postgresql
sudo systemctl status postgresql --no-pager
```

**Dấu hiệu đã xong:** trạng thái `active (running)`. Nếu nó không lên lại, gần như chắc chắn là
`shared_buffers` đặt quá lớn — xem `sudo tail -30 /var/log/postgresql/postgresql-17-main.log`.

---

## Bước 6 — Tailscale, rồi khoá cổng lại

Đây là bước chữa đúng cái đã làm hỏng lần trước: cổng 5432 phơi ra Internet, địa chỉ nhà mạng đổi là
mất kết nối, và cả thế giới quét được.

Cài trên **ba máy**: máy Linux này, máy chủ web MIGROUP-03 (có bản Windows), và máy làm việc của bạn.

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
tailscale ip -4        # → 100.x.x.x — chép lại
```

Quay lại `postgresql.conf`, thêm địa chỉ vừa lấy vào:

```
listen_addresses = 'localhost,100.x.x.x'
```

Nạp lại rồi khoá cổng:

```bash
sudo systemctl restart postgresql

sudo ufw allow 22/tcp
sudo ufw deny 5432/tcp
sudo ufw enable
sudo ufw status numbered
```

Địa chỉ `100.x.x.x` này **không bao giờ đổi** — kể cả khi nhà mạng đổi IP, khi máy chuyển nhà cung
cấp, hay khi bạn làm việc từ chỗ khác. Đó là lý do dùng nó trong chuỗi kết nối thay vì địa chỉ
công cộng.

**Dấu hiệu đã xong** — từ máy chủ web MIGROUP-03:

```powershell
Test-NetConnection -ComputerName 100.x.x.x -Port 5432
```

`TcpTestSucceeded : True` là thông. Đồng thời thử từ ngoài mạng riêng vào địa chỉ công cộng của
máy — phải **không** vào được.

---

## Bước 7 — Giới hạn tài nguyên

Chỉ làm bước này nếu máy còn chạy việc khác. Máy riêng cho Postgres thì bỏ qua.

```bash
sudo systemctl edit postgresql@17-main
```

Dán vào file mở ra:

```ini
[Service]
MemoryMax=1500M
CPUQuota=70%
```

```bash
sudo systemctl daemon-reload
sudo systemctl restart postgresql
```

Đặt `MemoryMax` cao hơn `shared_buffers` ít nhất gấp rưỡi, nếu không Postgres bị giết ngay khi có
truy vấn nặng.

---

## Bước 8 — Sao lưu tự động lên R2

Không có bước này thì mọi thứ ở trên chỉ là hoãn ngày mất dữ liệu.

```bash
sudo apt install -y awscli

sudo tee /usr/local/bin/sao-luu-chat.sh >/dev/null <<'EOF'
#!/bin/bash
set -euo pipefail
TEN="chat-$(date +%F-%H%M).sql.gz"
sudo -u postgres pg_dump -d "tourkit-chat" | gzip > "/tmp/$TEN"

# Dump hỏng mà vẫn đẩy lên là tệ hơn không có bản sao — dừng ở đây.
[ "$(stat -c%s "/tmp/$TEN")" -gt 10000 ] || { echo "DUMP RỖNG, dừng"; exit 1; }

AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... \
  aws s3 cp "/tmp/$TEN" "s3://TÊN-BUCKET/backup/$TEN" \
  --endpoint-url https://<accountid>.r2.cloudflarestorage.com

rm -f "/tmp/$TEN"
EOF

sudo chmod +x /usr/local/bin/sao-luu-chat.sh

echo "15 2 * * * root /usr/local/bin/sao-luu-chat.sh >>/var/log/sao-luu-chat.log 2>&1" \
  | sudo tee /etc/cron.d/sao-luu-chat
```

⚠️ **Bản sao chưa thử phục hồi thì chưa phải bản sao.** Rất nhiều nơi phát hiện file backup rỗng
đúng vào hôm cần đến nó. Làm một lần ngay tháng đầu:

```bash
sudo -u postgres createdb thu-phuc-hoi
gunzip -c /tmp/chat-....sql.gz | sudo -u postgres psql -d thu-phuc-hoi
sudo -u postgres psql -d thu-phuc-hoi -c "\dt"   # phải thấy chat_messages, chat_conversations…
sudo -u postgres dropdb thu-phuc-hoi
```

---

## Bước 9 — Cảnh báo trước khi chết

Đầy đĩa là nguyên nhân số một. Biết trước một ngày thì mất năm phút dọn dẹp; biết sau thì mất cả
buổi khôi phục.

```bash
sudo tee /etc/cron.d/canh-bao-dia >/dev/null <<'EOF'
0 * * * * root [ $(df --output=pcent / | tr -dc '0-9') -gt 85 ] && echo "Đĩa / còn dưới 15%" | logger -t dia-day
EOF
```

Và bật ghi lại truy vấn chậm, để biết chỗ nghẽn trước khi nó thành sự cố — thêm vào
`postgresql.conf`:

```
log_min_duration_statement = 2000   # ghi lại truy vấn chạy quá 2 giây
log_checkpoints = on
log_autovacuum_min_duration = 0
```

---

## Bước 10 — Nối ứng dụng vào

Chuỗi kết nối nằm ở **hai chỗ**, và sửa một chỗ thì hộp thư vẫn hỏng một nửa.

| Máy | File | Sau khi sửa |
|---|---|---|
| Máy làm việc | `appsettings.json` | Chạy lại ứng dụng |
| MIGROUP-03 | `C:\inetpub\wwwroot\Tour_AI\appsettings.json` | **Recycle App Pool** — không recycle thì vẫn dùng chuỗi cũ |

Chuỗi kết nối lưu dạng `ENC:` nên **không sửa tay được**. Sinh chuỗi mới bằng bộ mã hoá Crypton của
dự án (khoá và tham số nằm trong mã nguồn), hoặc nhờ Claude sinh hộ từ địa chỉ Tailscale + mật khẩu
mới.

**Dấu hiệu đã xong** — khởi động ứng dụng lần đầu, nó tự dựng toàn bộ bảng:

```bash
sudo -u postgres psql -d "tourkit-chat" -c "\dt" | head -20
```

Thấy `chat_messages`, `chat_conversations`, `chat_contacts`, `chat_outbox`… là ứng dụng đã vào được
và dựng schema xong. Mở hộp thư chat, nối lại một kênh, nhắn thử một câu.

---

## Phụ lục A — Lấy lại dữ liệu chat cũ

Máy GCP đang bị treo (suspended) chứ chưa xoá, nên đĩa còn nguyên. Muốn giữ hội thoại cũ thì làm
trước khi GCP tự chuyển sang dừng hẳn — giới hạn là **60 ngày** kể từ lúc treo.

```bash
# 1. Trên GCP Console: bấm RESUME (không phải Start)
gcloud compute instances resume TÊN-MÁY --zone=asia-southeast1-a

# 2. Địa chỉ công cộng ĐÃ ĐỔI khi treo — lấy địa chỉ mới
gcloud compute instances describe TÊN-MÁY --zone=asia-southeast1-a \
  --format="get(networkInterfaces[0].accessConfigs[0].natIP)"

# 3. SSH vào, kết xuất, tải về
sudo -u postgres pg_dump -d "tourkit-chat" | gzip > ~/chat-cu.sql.gz

# 4. Trên máy Linux mới: nhập vào
gunzip -c chat-cu.sql.gz | sudo -u postgres psql -d "tourkit-chat"
```

Nhập dữ liệu cũ **trước** khi khởi động ứng dụng lần đầu. Chạy app trước rồi mới nhập thì hai bộ
bảng đụng nhau.

---

## Phụ lục B — Sự cố thường gặp

| Triệu chứng | Nguyên nhân hay gặp nhất | Xử lý |
|---|---|---|
| App báo lỗi SSL khi kết nối | `ssl = off`, hoặc dùng `host` thay vì `hostssl` | Xem lại bước 3 và 4 |
| `permission denied for schema public` | Quên `GRANT ALL ON SCHEMA public` | Chạy lại dòng đó ở bước 2 |
| `no pg_hba.conf entry for host` | Địa chỉ nguồn không nằm trong dải đã khai | Kiểm `tailscale ip -4` ở máy gọi đến |
| Kết nối treo rồi timeout | Tường lửa, hoặc `listen_addresses` chưa có địa chỉ Tailscale | Bước 4 và 6 |
| Postgres không khởi động lại được | `shared_buffers` quá lớn so với RAM | Giảm theo bảng bước 5 |
| Chạy vài tuần rồi đột nhiên chết | Đầy đĩa | `df -h /`, dọn log, xem bước 9 |

---

## Vì sao sổ tay này khác một lần cài Postgres thông thường

Ba bước **6** (Tailscale + khoá cổng), **8** (sao lưu tự động) và **9** (cảnh báo đầy đĩa) không có
trong hướng dẫn cài Postgres bình thường. Chúng có mặt vì lần hỏng ngày 03/09/2026: máy đặt trên bản
dùng thử Google Cloud ở Singapore bị treo do hết hạn, cổng 5432 phơi ra Internet, không có bản sao
nào, và **không ai biết máy đã chết** cho tới lúc thử truy vấn — `/healthz` vẫn báo xanh vì nó không
kiểm tra kết nối cơ sở dữ liệu.

Bỏ ba bước đó đi là dựng lại đúng cái máy đã chết.
