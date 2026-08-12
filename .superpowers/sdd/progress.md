# Tour Price Catalog — tien do

Plan: docs/superpowers/plans/2026-07-15-tour-price-catalog.md
Nhanh: feature/tour-price-catalog (rebased lên main 2b1d267)

## Trạng thái (2026-07-18)

Đã thực thi Task 1–3, 5–7 (mọi task KHÔNG bị chặn). Build main+worker 0 error,
253 test pass (2 skip). App start sạch, bảng dbo.TourPriceCatalog tự tạo.

- [x] Task 1 — VietnameseText.Norm (1 nguồn) — commit e1e1395
      LƯU Ý: namespace TextUtil (KHÔNG "Text" như plan) vì che kiểu Wordprocessing.Text.
- [x] Task 2 — model CatalogRow + PriceCatalogRules (bóc sao/loại trừ) — commit fbd1ad7 (21 test)
- [x] Task 3 — schema dbo.TourPriceCatalog (bảng 22) + docs — commit 9ff7592
      LƯU Ý: escape "" trong comment SQL (SchemaSql là verbatim @").
- [ ] Task 4 — BLOCKER cross-repo (toutkit-app): GET /api/ai/provider-prices — CHƯA làm
- [x] Task 5 — TourKitNccClient.ProviderPricesAsync — commit c55c821
- [x] Task 6 — TourPriceCatalogRepository (Dapper MERGE) — commit 123e9cd
- [x] Task 7 — TourPriceCatalogSyncWorkflow + DI — commit f25e3d2
      SỬA so với plan: GetOrCreateServiceSessionAsync(tenantId, username, password)
      + lấy TenantServiceAccountStore.Get trước (như DealAutoReviewWorkflow).
- [ ] Task 8 — Nghiệm thu dữ liệu thật — CHẶN bởi Task 4 (cần deploy endpoint upstream)

## Còn lại
1. Task 4 ở repo toutkit-app (endpoint phân trang JOIN 4 bảng) → deploy lên api.travelai.vn.
2. Task 8: cấu hình workflow cho 1 tenant có service account → run-now → đối chiếu số.
3. Merge feature/tour-price-catalog → main (skill finishing-a-development-branch) sau khi
   Task 4+8 xong HOẶC merge phần mảng-1 proxy trước (Task 4 độc lập ở repo khác).

## Sau plan này: Mảng 2 (retriever + wizard) — plan riêng (spec §8.2).
