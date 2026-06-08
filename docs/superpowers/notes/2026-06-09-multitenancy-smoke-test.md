# Mail + Visa multi-tenancy smoke test report

Date: 2026-06-09. Tester: <fill khi chạy>. Build SHA: 0f558e7.

## Setup
- 2 TourKit accounts: userA (tenant T1), userB (tenant T2)
- DB rỗng (sau migration backup): `SELECT COUNT(*) FROM dbo.MailAccounts/Mails/VisaAssessments = 0`
- Proxy chạy local: `dotnet run --project TourkitAiProxy.csproj` (http://localhost:5080)
- TourKit backend trỏ về staging (config `TourKit:BaseUrl`)

## Test 1: Mail isolation
1. Login T1 → setup Gmail `info@t1.com` → `POST /api/v1/mail/sync` → verify N email
2. Logout, login T2 → vào `/mail` → EXPECT empty (chưa setup, không thấy email T1)
3. T2 setup Gmail `info@t2.com` → sync → verify chỉ email T2 (không lẫn email T1)
4. Login T1 lại → vào `/mail` → vẫn thấy đúng N email T1 (creds T1 không bị T2 đè)

## Test 2: Visa isolation
1. Login T1 → upload PDF → extract → score → verify 1 assessment trong `GET /api/v1/visa/assessments`
2. Logout, login T2 → vào `/visa/assessments` → EXPECT empty (không thấy assessment T1)
3. T2 thử `GET /api/v1/visa/assessments/{T1-assessment-id}` → EXPECT 404
4. T2 thử `DELETE /api/v1/visa/assessments/{T1-assessment-id}` → EXPECT 404 (KHÔNG xóa được, dữ liệu T1 còn nguyên khi login T1 lại)

## Test 3: Anonymous access blocked
KHÔNG có `X-Session-Id` header / không có `sessionId` query:
- `GET  /api/v1/mail`               → EXPECT 401
- `GET  /api/v1/mail/account`       → EXPECT 401
- `GET  /api/v1/visa/assessments`   → EXPECT 401
- `POST /api/v1/visa/assess`        → EXPECT 401

## Results
- **Test 1 (Mail isolation):** [Run với 2 tenant accounts — deferred to manual execution]
- **Test 2 (Visa isolation):** [Run với 2 tenant accounts — deferred to manual execution]
- **Test 3 (Anonymous blocked):** PASS (all 4 endpoints return 401, xem bảng dưới)

### Test 3 actual results (auto-run via curl, 2026-06-09, build 0f558e7)

| Endpoint                       | Method | Expected | Actual   | Result |
|--------------------------------|--------|----------|----------|--------|
| `/api/v1/mail`                 | GET    | 401      | HTTP 401 | PASS   |
| `/api/v1/mail/account`         | GET    | 401      | HTTP 401 | PASS   |
| `/api/v1/visa/assessments`     | GET    | 401      | HTTP 401 | PASS   |
| `/api/v1/visa/assess`          | POST   | 401      | HTTP 401 | PASS   |

Verdict: Anonymous (no `X-Session-Id` header) bị reject ở cả 4 endpoint nhạy cảm — middleware `RequireTenant` hoạt động đúng. Không có endpoint nào leak dữ liệu cho request thiếu session.

## Issues found
- Test 3: None — tất cả 4 endpoint trả 401 đúng kỳ vọng.
- Test 1 + Test 2: Chưa chạy (cần 2 tenant accounts thật) — deferred to manual execution by tester. Khi chạy, fill kết quả PASS/FAIL vào section Results và document issues (nếu có) ở đây.
