# Crypton Integration Guide

> Tài liệu này dành cho **đối tác tích hợp TRAV-AI Widget**. Mô tả cách mã hoá / giải mã token Crypton — cùng định dạng `/login-token` của TourKit.

## 1. Tổng quan

**Crypton** là helper AES-256/CBC dùng để **mã hoá credentials** trước khi gửi qua API (tránh lộ password plain-text trên đường truyền và logs). Token Crypton được dùng ở 2 endpoint:

| Endpoint | Mục đích |
|---|---|
| `POST /api/v1/login-token` | Đăng nhập TourKit qua Crypton token |
| `POST /api/v1/widget/init` | Tạo widget chat AI từ Crypton token (one-shot) |

Cùng 1 token có thể dùng cho cả 2.

---

## 2. Thông số thuật toán

> **KHÔNG đổi các hằng số dưới đây** — server và client phải khớp 100% mới giải mã được.

| Thông số | Giá trị |
|---|---|
| Algorithm | AES |
| Mode | CBC |
| Key size | 256 bits (32 bytes) |
| Padding | PKCS7 |
| IV (Initialization Vector) | `@1B2c3D4e5F6g7H8` (16 bytes ASCII) |
| PassPhrase | `Pas5pr@se` |
| Salt | `s@1tValue` |
| Key derivation | **PasswordDeriveBytes** (PBKDF1 + MS extension) |
| Hash | SHA-1 |
| Iterations | 2 |
| Output | Base64 |

### Lưu ý quan trọng về key derivation

`PasswordDeriveBytes` là **biến thể PBKDF1 của Microsoft** (KHÔNG phải PBKDF2 chuẩn).

Thuật toán:
1. Khởi tạo: `h = SHA1(passPhrase + salt)`
2. Lặp (iterations - 1) lần: `h = SHA1(h)`
3. Lấy 20 bytes đầu của `h`
4. Nếu cần thêm bytes (key size > 20): với counter = 1, 2, 3...:
   - `extended = SHA1(str(counter) + h)`
   - Lấy thêm 20 bytes
5. Cắt đúng `keySize / 8` bytes (32 bytes cho AES-256)

---

## 3. Định dạng payload

Plain-text JSON trước khi encrypt:

```json
{
  "username": "admin",
  "password": "your-password",
  "domain":   "bayviet"
}
```

3 field bắt buộc:
- `username` — TourKit username
- `password` — TourKit password (plaintext, sẽ được Crypton-encrypt)
- `domain` — TenantId (vd `bayviet`, `staging.tourkit.vn`, ...)

Sau khi `Crypton.Encrypt(JSON.stringify(payload))` → ra chuỗi Base64 dùng làm `token`.

---

## 4. Reference implementations

### 4.1 C# .NET Framework 4.8 (WebForms / MVC5 / Console)

> Code dưới dùng cú pháp C# 7.3 mặc định của .NET Framework 4.8 (KHÔNG cần bật C# 8+ trong csproj). Không có pragma `SYSLIB0041` vì warning đó chỉ xuất hiện ở .NET 6+.

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class Crypton
{
    private const string PassPhrase = "Pas5pr@se";
    private const string SaltValue  = "s@1tValue";
    private const string InitVector = "@1B2c3D4e5F6g7H8";
    private const int    KeySize    = 256;
    private const int    Iterations = 2;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        byte[] ivBytes    = Encoding.ASCII.GetBytes(InitVector);
        byte[] saltBytes  = Encoding.ASCII.GetBytes(SaltValue);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        // PasswordDeriveBytes là PBKDF1 + MS extension — cùng thuật toán proxy server dùng.
        using (var password = new PasswordDeriveBytes(PassPhrase, saltBytes, "SHA1", Iterations))
        {
            byte[] keyBytes = password.GetBytes(KeySize / 8);

            using (var aes = new RijndaelManaged())
            {
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor(keyBytes, ivBytes))
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    {
                        cs.Write(plainBytes, 0, plainBytes.Length);
                        cs.FlushFinalBlock();
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        byte[] cipherBytes = Convert.FromBase64String(cipherText);
        byte[] ivBytes     = Encoding.ASCII.GetBytes(InitVector);
        byte[] saltBytes   = Encoding.ASCII.GetBytes(SaltValue);

        using (var password = new PasswordDeriveBytes(PassPhrase, saltBytes, "SHA1", Iterations))
        {
            byte[] keyBytes = password.GetBytes(KeySize / 8);

            using (var aes = new RijndaelManaged())
            {
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor(keyBytes, ivBytes))
                using (var ms = new MemoryStream(cipherBytes))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var output = new MemoryStream())
                {
                    cs.CopyTo(output);
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }
    }
}
```

**Sử dụng** (cần `Newtonsoft.Json` qua NuGet vì .NET 4.8 không có `System.Text.Json` mặc định):

```csharp
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;

var payload = JsonConvert.SerializeObject(new {
    username = "admin",
    password = "your-password",
    domain   = "bayviet"
});
var token = Crypton.Encrypt(payload);

// POST /api/v1/widget/init
using (var http = new HttpClient())
{
    var body = JsonConvert.SerializeObject(new { token, botName = "Trợ lý Bay Việt" });
    var resp = await http.PostAsync(
        "https://ai.tourkit.vn/api/v1/widget/init",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var json = await resp.Content.ReadAsStringAsync();
    // → { token, embedSnippet, ... }
}
```

> **Lưu ý .NET 4.8:**
> - `RijndaelManaged` được dùng thay `Aes.Create()` để khớp hành vi cũ (vẫn đúng AES-256 vì BlockSize default = 128). `Aes.Create()` cũng OK nếu môi trường có (.NET 4.6.2+).
> - `PasswordDeriveBytes` KHÔNG bị `[Obsolete]` trên .NET Framework — không cần `#pragma warning disable`.
> - `await` cần method có `async` modifier; nếu chạy trong WebForms cần `<%@ Page Async="true" %>`.

### 4.2 Node.js (>=14)

```javascript
const crypto = require('crypto');

const PASS_PHRASE = 'Pas5pr@se';
const SALT = 's@1tValue';
const IV = Buffer.from('@1B2c3D4e5F6g7H8', 'ascii');
const KEY_SIZE = 32;        // 256 / 8
const ITERATIONS = 2;

/** PasswordDeriveBytes — MS-compatible PBKDF1 + extension */
function passwordDeriveBytes(passphrase, salt, iterations, keyLength) {
  let h = crypto.createHash('sha1')
    .update(Buffer.concat([Buffer.from(passphrase, 'utf8'), Buffer.from(salt, 'ascii')]))
    .digest();
  for (let i = 1; i < iterations; i++) {
    h = crypto.createHash('sha1').update(h).digest();
  }
  const result = Buffer.alloc(keyLength);
  const firstLen = Math.min(20, keyLength);
  h.copy(result, 0, 0, firstLen);
  let pos = 20;
  let counter = 1;
  while (pos < keyLength) {
    const extended = crypto.createHash('sha1')
      .update(Buffer.concat([Buffer.from(String(counter), 'ascii'), h]))
      .digest();
    const copyLen = Math.min(20, keyLength - pos);
    extended.copy(result, pos, 0, copyLen);
    pos += copyLen;
    counter++;
  }
  return result;
}

function encrypt(plainText) {
  const key = passwordDeriveBytes(PASS_PHRASE, SALT, ITERATIONS, KEY_SIZE);
  const cipher = crypto.createCipheriv('aes-256-cbc', key, IV);
  const ct = Buffer.concat([cipher.update(plainText, 'utf8'), cipher.final()]);
  return ct.toString('base64');
}

function decrypt(cipherText) {
  if (!cipherText) return '';
  const key = passwordDeriveBytes(PASS_PHRASE, SALT, ITERATIONS, KEY_SIZE);
  const decipher = crypto.createDecipheriv('aes-256-cbc', key, IV);
  const pt = Buffer.concat([decipher.update(Buffer.from(cipherText, 'base64')), decipher.final()]);
  return pt.toString('utf8');
}

module.exports = { encrypt, decrypt };
```

**Sử dụng:**
```javascript
const { encrypt } = require('./crypton');
const payload = JSON.stringify({
  username: 'admin', password: 'your-password', domain: 'bayviet'
});
const token = encrypt(payload);

const resp = await fetch('https://ai.tourkit.vn/api/v1/widget/init', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ token, botName: 'Trợ lý Bay Việt' }),
});
const { token: widgetToken, embedSnippet } = await resp.json();
console.log(embedSnippet);
```

### 4.3 Python (>=3.7)

```python
import hashlib
import base64
import json
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.padding import PKCS7

PASS_PHRASE = 'Pas5pr@se'
SALT = 's@1tValue'
IV = b'@1B2c3D4e5F6g7H8'
KEY_SIZE = 32
ITERATIONS = 2


def password_derive_bytes(passphrase: str, salt: str, iterations: int, key_length: int) -> bytes:
    """MS PasswordDeriveBytes — PBKDF1 + extension."""
    h = hashlib.sha1(passphrase.encode('utf-8') + salt.encode('ascii')).digest()
    for _ in range(iterations - 1):
        h = hashlib.sha1(h).digest()

    result = bytearray(key_length)
    first_len = min(20, key_length)
    result[0:first_len] = h[0:first_len]

    pos = 20
    counter = 1
    while pos < key_length:
        extended = hashlib.sha1(str(counter).encode('ascii') + h).digest()
        copy_len = min(20, key_length - pos)
        result[pos:pos + copy_len] = extended[0:copy_len]
        pos += copy_len
        counter += 1
    return bytes(result)


def encrypt(plain_text: str) -> str:
    key = password_derive_bytes(PASS_PHRASE, SALT, ITERATIONS, KEY_SIZE)
    cipher = Cipher(algorithms.AES(key), modes.CBC(IV))
    encryptor = cipher.encryptor()

    padder = PKCS7(128).padder()
    padded = padder.update(plain_text.encode('utf-8')) + padder.finalize()
    ct = encryptor.update(padded) + encryptor.finalize()
    return base64.b64encode(ct).decode('ascii')


def decrypt(cipher_text: str) -> str:
    if not cipher_text:
        return ''
    key = password_derive_bytes(PASS_PHRASE, SALT, ITERATIONS, KEY_SIZE)
    cipher = Cipher(algorithms.AES(key), modes.CBC(IV))
    decryptor = cipher.decryptor()
    ct = base64.b64decode(cipher_text)
    padded = decryptor.update(ct) + decryptor.finalize()

    unpadder = PKCS7(128).unpadder()
    pt = unpadder.update(padded) + unpadder.finalize()
    return pt.decode('utf-8')


if __name__ == '__main__':
    import requests
    payload = json.dumps({
        'username': 'admin', 'password': 'your-password', 'domain': 'bayviet'
    })
    token = encrypt(payload)

    r = requests.post('https://ai.tourkit.vn/api/v1/widget/init', json={
        'token': token, 'botName': 'Trợ lý Bay Việt'
    })
    data = r.json()
    print('Widget token:', data['token'])
    print('Embed snippet:', data['embedSnippet'])
```

**Yêu cầu:** `pip install cryptography requests`

---

## 5. Test vectors

Dùng để **xác minh implementation của bạn khớp với server** — nếu encrypt cùng plaintext mà ra cùng cipher → implementation đúng. *(AES-CBC deterministic vì IV cố định.)*

| Plaintext | Cipher (Base64) |
|---|---|
| `hello` | `aexSJ5eV7cT55CVFmi5VJA==` |
| `{"username":"demo","password":"demo123","domain":"demo-tenant"}` | `aCfYeKeyyY5ANOaNBjJcdxYhoy/Ip5kMKEXAHu0yA/GxlWiRKDcGim2RJZpl7Qzx7o2qdMcxZTy9brjbKMHZjg==` |

### Verify nhanh bằng dev endpoint

Server có sẵn 2 endpoint dev (chỉ chạy khi `ASPNETCORE_ENVIRONMENT=Development`):

```bash
# Encrypt
curl -X POST http://localhost:5080/api/v1/widget/_dev/crypton/encrypt \
  -H "Content-Type: application/json" \
  -d '{"plain":"hello"}'
# → {"cipher":"aexSJ5eV7cT55CVFmi5VJA=="}

# Decrypt
curl -X POST http://localhost:5080/api/v1/widget/_dev/crypton/decrypt \
  -H "Content-Type: application/json" \
  -d '{"plain":"aexSJ5eV7cT55CVFmi5VJA=="}'
# → {"plain":"hello"}
```

> **Production:** 2 endpoint dev này tự ẩn — không thể gọi được khi deploy.

---

## 6. Tích hợp với `/api/v1/widget/init`

### Request

```http
POST /api/v1/widget/init HTTP/1.1
Content-Type: application/json

{
  "token": "<Crypton-encrypted JSON {username,password,domain}>",

  "botName":         "Trợ lý Bay Việt",
  "greeting":        "Xin chào Anh/Chị!",
  "systemPrompt":    "Bạn là trợ lý của Bay Việt, chuyên tour Châu Âu...",
  "color":           "#F97316",
  "allowedOrigins":  ["https://bayviet.vn", "*.bayviet.vn"],
  "allowedTools":    ["tours", "list_markets", "booking_tickets"],
  "cacheTtlSeconds": 300,
  "linkCrm":         true
}
```

| Field | Bắt buộc | Default | Mô tả |
|---|---|---|---|
| `token` | ✅ | — | Crypton(JSON{username,password,domain}) |
| `botName` | ❌ | `"Trợ lý TRAV-AI"` | Tên bot hiển thị (≤128) |
| `greeting` | ❌ | (default VN) | Câu chào đầu (≤1024) |
| `systemPrompt` | ❌ | (default tour FAQ) | Định nghĩa bot (≤8000) |
| `color` | ❌ | `#F97316` | Hex 6 ký tự |
| `allowedOrigins` | ❌ | null (mọi nơi) | Whitelist domain embed |
| `allowedTools` | ❌ | `["tours","list_markets","booking_tickets"]` | Tool CRM bot được gọi |
| `cacheTtlSeconds` | ❌ | 300 | Cache CRM data |
| `linkCrm` | ❌ | true | Auto link CRM với session từ creds |

### Response (200 OK)

```json
{
  "token":        "trav_ee5e7be0d8cf18c495f0e3d873f69d17",
  "embedSnippet": "<script async src=\"https://ai.tourkit.vn/widget.js\"\n  data-token=\"trav_ee5e7be0d8cf18c495f0e3d873f69d17\"></script>",
  "botName":      "Trợ lý Bay Việt",
  "color":        "#F97316",
  "tenantId":     "bayviet",
  "crmLinked":    true,
  "allowedTools": ["tours", "list_markets", "booking_tickets"]
}
```

### Error responses

| Trường hợp | Status | Body |
|---|---|---|
| Thiếu `token` | 400 | `{"error":"Thiếu token"}` |
| Token sai base64 / hỏng | 400 | `{"error":"Init thất bại: Token TourKit không hợp lệ hoặc giải mã thất bại"}` |
| Token decrypt ra không phải JSON | 400 | `{"error":"Init thất bại: Nội dung token không phải JSON {username,password,domain}"}` |
| JSON thiếu field | 400 | `{"error":"Init thất bại: Token thiếu username/password/domain"}` |
| Sai username/password | 401 | `{"error":"Init thất bại: <message từ TourKit>"}` |
| TourKit upstream lỗi | 502 | `{"error":"Init thất bại: <message>"}` |

---

## 7. End-to-end integration example

Quy trình onboarding 1 khách hàng mới:

```javascript
// Trong TourKit CMS hoặc admin portal của bạn
const { encrypt } = require('./crypton');

async function onboardWidget(tenant) {
  // 1. Tạo Crypton token từ creds TourKit của tenant
  const token = encrypt(JSON.stringify({
    username: tenant.adminUsername,
    password: tenant.adminPassword,
    domain:   tenant.id,
  }));

  // 2. Gọi /init — backend login + tạo widget + link CRM trong 1 call
  const res = await fetch('https://ai.tourkit.vn/api/v1/widget/init', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      token,
      botName:      `Trợ lý ${tenant.companyName}`,
      greeting:     `Xin chào Anh/Chị! Em là trợ lý của ${tenant.companyName}.`,
      systemPrompt: `Bạn là trợ lý của ${tenant.companyName}, ${tenant.businessDescription}.`,
      color:        tenant.brandColor,
      allowedOrigins: [`https://${tenant.domain}`, `*.${tenant.domain}`],
    }),
  });
  const data = await res.json();
  if (data.error) throw new Error(data.error);

  // 3. Lưu widget token (trav_xxx) + snippet vào tenant settings
  await db.tenants.update(tenant.id, {
    widgetToken:    data.token,
    widgetSnippet:  data.embedSnippet,
  });

  // 4. Hiển thị snippet trong UI cho khách copy
  return data.embedSnippet;
}
```

---

## 8. Bảo mật

- ✅ **Password không gửi plain qua API** — luôn Crypton-encrypt trước khi POST
- ✅ **Cross-tenant guard server-side** — `/init` tự derive tenant từ token, không nhận từ client
- ✅ **JWT TourKit không bao giờ ra client** — chỉ giữ server-side trong TkSessionStore
- ⚠️ **Crypton key chia sẻ giữa server + đối tác** — đối tác giữ kín các hằng số (passphrase/salt/IV) trong code, KHÔNG hardcode vào client-side JS công khai. Hợp lý nhất: backend đối tác encrypt, không expose ra browser.
- ⚠️ **AES-CBC IV cố định** — KHÔNG nhược điểm khi mỗi payload có nonce ngầm (password thay đổi giữa requests). Nếu plaintext giống hệt → ciphertext giống hệt (deterministic). Trong context auth: KHÔNG cần random IV vì credentials thay đổi mỗi tenant.

---

## 9. Troubleshooting

| Triệu chứng | Nguyên nhân thường gặp |
|---|---|
| Test vector `hello` của bạn ra cipher khác | Sai PassPhrase / Salt / IV / Iterations / KeySize. So sánh từng hằng số. |
| Decrypt ra rỗng / lỗi padding | Sai key derivation. PBKDF1 ≠ PBKDF2 — phải implement chính xác MS extension. |
| `/init` trả 400 "không phải JSON {username,password,domain}" | Payload decode ra không đúng shape — kiểm tra JSON serialize, lưu ý `domain` không phải `tenantId`. |
| `/init` trả 401 "Đăng nhập TourKit thất bại" | Username/password trong token sai, hoặc tenant không tồn tại trên TourKit. |
| `/init` trả 502 | TourKit upstream (`mobile-api.tourkit.vn` / staging) không reach được. Liên hệ admin. |

---

**Liên hệ hỗ trợ:** `support@tourkit.vn`
