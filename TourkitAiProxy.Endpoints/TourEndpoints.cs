using System.Collections.Concurrent;
using System.Text.Json;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services.Store;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Wizard server-side: nháp tour (Redis/file theo tenant) + NCC thật (proxy TourKit).
///
///   GET    /api/v1/tours                       — list nháp tour của công ty
///   GET    /api/v1/tours/{id}                  — 1 nháp
///   POST   /api/v1/tours                       — lưu nháp (tự sinh id nếu thiếu)
///   DELETE /api/v1/tours/{id}                  — xoá nháp
///   GET    /api/v1/ncc/categories              — loại dịch vụ (Khách sạn, Vận chuyển…)
///   GET    /api/v1/ncc/providers?serviceId=|?marketId= — NCC theo loại DV / theo thị trường
///   GET    /api/v1/ncc/providers/{id}/services?categoryId= — bảng giá hợp đồng của 1 NCC
public static class TourEndpoints
{
    private const string COLL = "tours";

    // Cache thị trường per-tenant — đổi chậm, TTL 6h là an toàn.
    private static readonly ConcurrentDictionary<string, (List<string> Names, DateTime Exp)> _marketsCache = new();
    private static readonly TimeSpan MarketsTtl = TimeSpan.FromHours(6);

    // Cache thống kê chất lượng dữ liệu NCC per-tenant (R1) — quét toàn bộ NCC nên nặng → TTL 10 phút.
    private static readonly ConcurrentDictionary<string, (object Stats, DateTime Exp)> _nccStatsCache = new();
    private static readonly TimeSpan NccStatsTtl = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapTourEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── Nháp tour ───────────────────────────────────────────────────────────
        v1.MapGet("/tours", (HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            var list = store.List<SavedTour>(COLL, s.TenantId)
                .OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal).ToList();
            return Results.Json(list);
        });

        v1.MapGet("/tours/{id}", (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            var t = store.Get<SavedTour>(COLL, s.TenantId, id);
            return t == null ? Results.NotFound(new { error = "Không tìm thấy nháp tour" }) : Results.Json(t);
        });

        v1.MapPost("/tours", async (JsonElement body, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var s = sessions.Get(sid);
            if (s == null) return Unauthorized();
            // Gate quyền TẠO TOUR (sheet bug 105): đại lý/CTV chỉ có quyền đặt chỗ thì không được
            // dựng báo giá. Kiểm ở SERVER chứ không chỉ ẩn menu — ẩn menu vẫn gọi API tay được.
            if (!await SessionAuth.CanCreateTourAsync(sid!, sessions, ctx.RequestAborted))
                return SessionAuth.ForbiddenCreateTour();

            var id = GetStr(body, "id");
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
            // Re-save (cùng id) KHÔNG reset status/createdAt — giữ nguyên từ bản cũ.
            var existing = store.Get<SavedTour>(COLL, s.TenantId, id!);
            var tour = new SavedTour(
                Id: id!,
                Title: GetStr(body, "title"),
                Request: Clone(body, "request"),
                Itinerary: Clone(body, "itinerary"),
                Marketing: Clone(body, "marketing"),
                Rows: Clone(body, "rows"),
                NccCoveragePct: GetInt(body, "nccCoveragePct") ?? 0,
                CreatedAt: existing?.CreatedAt ?? DateTime.UtcNow.ToString("o"),
                CreatedBy: existing?.CreatedBy ?? (s.FullName ?? s.Username),
                Status: GetStr(body, "status") ?? existing?.Status ?? "draft",
                // Thông tin đồng bộ CRM chỉ do endpoint /save-crm ghi — auto-save của wizard KHÔNG
                // được xoá nó, nếu không bấm Sửa một cái là mất dấu đơn đã tạo và lần sau tạo trùng.
                CrmTourId: existing?.CrmTourId,
                CrmTourType: existing?.CrmTourType,
                CrmTourCode: existing?.CrmTourCode,
                CrmSyncedAt: existing?.CrmSyncedAt);
            store.Set(COLL, s.TenantId, id!, tour);
            return Results.Json(new { ok = true, id, tour });
        });

        // PATCH /tours/{id}/status — đổi trạng thái nháp tour (draft|sent|success) → badge Wizard landing.
        v1.MapPatch("/tours/{id}/status", async (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var s = sessions.Get(sid);
            if (s == null) return Unauthorized();
            if (!await SessionAuth.CanCreateTourAsync(sid!, sessions, ctx.RequestAborted))
                return SessionAuth.ForbiddenCreateTour();
            string? status = null;
            try
            {
                var b = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("status", out var v) && v.ValueKind == JsonValueKind.String)
                    status = v.GetString();
            }
            catch { }
            status = (status ?? "").Trim().ToLowerInvariant();
            if (status != "draft" && status != "sent" && status != "success")
                return Results.BadRequest(new { error = "status phải là draft|sent|success" });
            var t = store.Get<SavedTour>(COLL, s.TenantId, id);
            if (t == null) return Results.NotFound(new { error = "Không tìm thấy nháp tour" });
            store.Set(COLL, s.TenantId, id, t with { Status = status });
            return Results.Json(new { ok = true, id, status });
        });

        v1.MapDelete("/tours/{id}", async (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var s = sessions.Get(sid);
            if (s == null) return Unauthorized();
            if (!await SessionAuth.CanCreateTourAsync(sid!, sessions, ctx.RequestAborted))
                return SessionAuth.ForbiddenCreateTour();
            return store.Delete(COLL, s.TenantId, id) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Không tìm thấy" });
        });

        // ─── Đồng bộ nháp tour → ĐƠN THẬT trên CRM (sheet bug 103 + 104) ──────────────────────
        // POST /tours/{id}/save-crm  body { tourType: 3|2, customerName?, customerPhone?, customerEmail? }
        //   3 = GIT (tour đoàn)     → TourKit.Api POST /api/ai/tours     → tours + Orders + tour_customers,
        //                             khách hàng find-or-create theo SĐT ⇒ SĐT là BẮT BUỘC.
        //   2 = FIT (tour khách lẻ) → TourKit.Api POST /api/tours/sample → tour_samples (không gắn khách).
        // Trước đây màn Tính giá Tour chỉ ghi Redis + dbo.TourQuotes, không có đường nào sang CRM nên
        // báo giá dựng xong "không biết đi đâu về đâu".
        v1.MapPost("/tours/{id}/save-crm", async (string id, HttpContext ctx, TenantStore store,
            TkSessionStore sessions, TourKitApiClient api, ILogger<EndpointsLog> log) =>
        {
            var sid = Sid(ctx);
            var s = sessions.Get(sid);
            if (s == null) return Unauthorized();

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "Body JSON không hợp lệ" }); }

            var tourType = GetInt(body, "tourType") ?? 3;
            if (tourType is not (2 or 3))
                return Results.BadRequest(new { error = "tourType chỉ nhận 3 (GIT) hoặc 2 (FIT)" });

            // Quyền theo ĐÚNG loại đơn sắp tạo — có quyền tạo FIT không có nghĩa là được tạo GIT.
            // CRM còn kiểm lại lần nữa; chặn ở đây để báo lỗi tử tế thay vì 400 khó hiểu từ upstream.
            await sessions.EnsurePermissionsAsync(sid!, ctx.RequestAborted);
            var need = tourType == 3 ? TkPermissionCodes.TaoTourGit : TkPermissionCodes.TaoTourFit;
            if (!sessions.HasPermission(sid, need))
                return Results.Json(new { error = $"Bạn không có quyền tạo tour {(tourType == 3 ? "GIT" : "FIT")} ({need})." }, statusCode: 403);

            var tour = store.Get<SavedTour>(COLL, s.TenantId, id);
            if (tour == null) return Results.NotFound(new { error = "Không tìm thấy nháp tour" });
            if (tour.CrmTourId > 0)
                return Results.Json(new
                {
                    error = $"Nháp này đã lên CRM rồi ({tour.CrmTourCode}) — sửa tiếp bên CRM để không tạo đơn trùng.",
                    crmTourId = tour.CrmTourId, crmTourType = tour.CrmTourType, crmTourCode = tour.CrmTourCode
                }, statusCode: 409);

            var req = tour.Request;
            var mk = tour.Marketing;

            var title = Clean(GetStr(mk, "tourName") ?? tour.Title ?? GetStr(req, "route"));
            if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Nháp chưa có tên tour" });

            var adults = GetInt(req, "adults") ?? 0;
            var children = GetInt(req, "children") ?? 0;
            var pax = Math.Max(adults + children, 1);
            var days = Math.Max(GetInt(req, "days") ?? 1, 1);
            var note = GetStr(req, "notes");

            if (!DateTime.TryParse(GetStr(req, "startDate"), out var departure))
                return Results.BadRequest(new { error = "Nháp chưa có ngày khởi hành — điền ở Bước 1 rồi đồng bộ lại" });
            var endDate = departure.AddDays(days - 1);

            // Giá bán mỗi dòng = priceNet × (1 + markup) × (1 + VAT) — ĐÚNG công thức Bước 3/4 đang
            // hiện cho khách (steps/step4.jsx). Đổi công thức ở đây là số trên CRM lệch số đã báo giá.
            var lines = new List<(string Title, decimal PreVat, decimal Vat)>();
            decimal totalSale = 0m;
            if (tour.Rows.ValueKind == JsonValueKind.Array)
                foreach (var r in tour.Rows.EnumerateArray())
                {
                    var net = Dec(r, "priceNet");
                    if (net <= 0) continue;
                    var vat = Dec(r, "vat");
                    var preVat = decimal.Round(net * (1 + Dec(r, "markup") / 100m), 0);
                    lines.Add((Clean(GetStr(r, "service")) ?? "Dịch vụ", preVat, vat));
                    totalSale += preVat * (1 + vat / 100m);
                }
            if (lines.Count == 0) return Results.BadRequest(new { error = "Bảng tính giá đang trống — quay lại Bước 3" });

            // Gọi upstream + tự re-login khi JWT hết hạn (cùng cách tour-builder/save-crm).
            async Task<JsonElement> Post(string path, object payload)
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                try { return await api.PostAsync(jwt, path, payload, ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid!, ctx.RequestAborted);
                    return await api.PostAsync(jwt, path, payload, ctx.RequestAborted);
                }
            }
            async Task<JsonElement> Get(string path)
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                try { return await api.GetAsync(jwt, path, ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid!, ctx.RequestAborted);
                    return await api.GetAsync(jwt, path, ctx.RequestAborted);
                }
            }

            try
            {
                JsonElement data;
                if (tourType == 3)
                {
                    // ── GIT ───────────────────────────────────────────────────────────────────
                    var custName = Clean(GetStr(body, "customerName") ?? GetStr(req, "customerName"));
                    var custPhone = new string((GetStr(body, "customerPhone") ?? GetStr(req, "customerPhone") ?? "")
                                               .Where(char.IsDigit).ToArray());
                    if (string.IsNullOrWhiteSpace(custName) || custPhone.Length == 0)
                        return Results.BadRequest(new { error = "Tour GIT cần Tên + SĐT khách hàng (CRM gắn đơn vào khách theo SĐT)" });

                    data = await Post("/api/ai/tours", new
                    {
                        crmTourId = 0,
                        title,
                        marketName = (string?)null,
                        startDate = departure.ToString("yyyy-MM-dd"),
                        endDate = endDate.ToString("yyyy-MM-dd"),
                        adultCount = adults,
                        childCount = children,
                        customerName = custName,
                        customerPhone = custPhone,
                        customerEmail = Clean(GetStr(body, "customerEmail") ?? GetStr(req, "customerEmail")),
                        note,
                        revenues = lines.Select(l => new { title = l.Title, unitPrice = l.PreVat, quantity = 1m, vatPercent = l.Vat }).ToList(),
                    });
                }
                else
                {
                    // ── FIT ───────────────────────────────────────────────────────────────────
                    // Mã tour do CRM sinh (cùng hàm web dùng) — không tự đặt mã ở proxy để khỏi lệch cấu hình.
                    var codeData = await Get("/api/tours/next-sample-code");
                    var tourCode = GetStr(codeData, "maYeuCau") ?? GetStr(codeData, "MaYeuCau");
                    if (string.IsNullOrWhiteSpace(tourCode))
                        return Results.Json(new { error = "CRM không cấp được mã tour FIT" }, statusCode: 502);

                    // Điểm đón/trả bắt buộc: tách từ "Hà Nội - Quy Nhơn". Không có dấu "-" thì dùng
                    // nguyên chuỗi cho cả hai đầu — user sửa lại trên CRM, còn hơn chặn không cho tạo.
                    var route = Clean(GetStr(req, "route")) ?? title;
                    var parts = route.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var pickup = parts.Length > 0 ? parts[0] : route;
                    var pickdown = parts.Length > 1 ? parts[1] : route;

                    var today = DateTime.Today;
                    var bookingDays = departure >= today ? departure : today;   // ValidateSampleRequest: nhận chỗ ≤ ngừng nhận chỗ

                    data = await Post("/api/tours/sample", new
                    {
                        tourCode,
                        title,
                        departureDate = departure.ToString("yyyy-MM-dd"),
                        endDate = endDate.ToString("yyyy-MM-dd"),
                        slots = (short)Math.Min(pax, short.MaxValue),
                        transportation = GuessTransportation(tour.Itinerary),
                        // Giá NGƯỜI LỚN = giá trọn gói/khách. Giá tour = đúng con số đó, giảm giá 0 —
                        // khớp quy ước web "Giá người lớn = Giá tour − Giảm giá".
                        tourPrice = decimal.Round(totalSale / pax, 0),
                        discount = 0m,
                        pricePerSlot = decimal.Round(totalSale / pax, 0),
                        placePickup = pickup,
                        placePickdown = pickdown,
                        bookingDate = today.ToString("yyyy-MM-dd"),
                        bookingDays = bookingDays.ToString("yyyy-MM-dd"),
                        typeOf = "Khác",
                        note,
                    });
                }

                var crmTourId = GetInt(data, "tourId") ?? GetInt(data, "TourId") ?? 0;
                var crmCode = GetStr(data, "tourCode") ?? GetStr(data, "TourCode")
                              ?? GetStr(data, "maYeuCau") ?? GetStr(data, "MaYeuCau");

                // Ghi dấu lên nháp: lần bấm thứ hai sẽ bị chặn 409 thay vì đẻ đơn trùng.
                store.Set(COLL, s.TenantId, id, tour with
                {
                    CrmTourId = crmTourId,
                    CrmTourType = tourType,
                    CrmTourCode = crmCode,
                    CrmSyncedAt = DateTime.UtcNow.ToString("o"),
                    Status = "success",
                });

                log.LogInformation("[wizard] save-crm OK tenant={T} draft={D} type={Type} crmTourId={Id} code={Code}",
                    s.TenantId, id, tourType, crmTourId, crmCode);
                return Results.Json(new { ok = true, tourType, crmTourId, crmTourCode = crmCode, result = data });
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex)
            {
                log.LogError(ex, "[wizard] save-crm draft={D} type={Type}", id, tourType);
                return Results.Json(new { error = "Đồng bộ CRM lỗi: " + ex.Message }, statusCode: 500);
            }
        }).DisableAntiforgery();

        // ─── NCC thật (proxy TourKit) ──────────────────────────────────────────────
        v1.MapGet("/ncc/categories", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.CategoriesAsync(sid!, ctx.RequestAborted));
        });

        v1.MapGet("/ncc/providers", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, int? serviceId, int? marketId) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            if (serviceId.HasValue) return await Proxy(() => ncc.ProvidersByServiceAsync(sid!, serviceId.Value, ctx.RequestAborted));
            return await Proxy(() => ncc.ProvidersAsync(sid!, marketId, ctx.RequestAborted));
        });

        v1.MapGet("/ncc/providers/{id:int}/services", async (int id, HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, int? categoryId) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.ProviderServicesAsync(sid!, id, categoryId, ctx.RequestAborted));
        });

        // Danh sách NCC để HIỂN THỊ (search + paging) — proxy /api/providers (endpoint mới). Cho trang "Nhà cung cấp".
        // Query params: filter (keyword), pageIndex, pageSize, serviceId (optional — filter theo loại DV).
        v1.MapGet("/ncc/list", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, string? filter, int? pageIndex, int? pageSize, int? serviceId, int? sortOrder) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.ProviderListAsync(sid!, filter, pageIndex ?? 1, pageSize ?? 20, serviceId, ctx.RequestAborted, sortOrder ?? 0));
        });

        // R1 (Sheet BugTRAV-AI): thống kê "nâng cao chất lượng dữ liệu" cho banner màn NCC list.
        // Đếm TỔNG toàn bộ NCC → thiếu email / thiếu SĐT (quét /api/ai/providers) + NCC chưa có bảng giá
        // (distinct providerId từ /api/ai/provider-prices → thiếu = total - có). Nặng nên cache 10 phút/tenant.
        v1.MapGet("/ncc/stats", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, ILogger<EndpointsLog> log) =>
        {
            var sid = Sid(ctx); var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            if (_nccStatsCache.TryGetValue(sess.TenantId, out var c) && c.Exp > DateTime.UtcNow)
                return Results.Json(c.Stats);
            try
            {
                const int PS = 500, MAXPAGES = 60;  // trần an toàn ~30k NCC
                int total = 0, missingEmail = 0, missingPhone = 0;
                for (int page = 1; page <= MAXPAGES; page++)
                {
                    var d = await ncc.ProviderListAsync(sid!, null, page, PS, null, ctx.RequestAborted);
                    if (page == 1) total = GetIntProp(d, "total");
                    var items = GetArrayItems(d, "items");
                    if (items.Count == 0) break;
                    foreach (var it in items)
                    {
                        if (IsBlankProp(it, "email")) missingEmail++;
                        if (IsBlankProp(it, "phone")) missingPhone++;
                    }
                    if ((long)page * PS >= total) break;
                }

                var withPrice = new HashSet<int>();
                for (int page = 0; page < MAXPAGES; page++)  // provider-prices dùng pageIndex 0-based
                {
                    var d = await ncc.ProviderPricesAsync(sid!, page, PS, ctx.RequestAborted);
                    var items = GetArrayItems(d, "items");
                    if (items.Count == 0) break;
                    foreach (var it in items)
                    {
                        var pid = GetIntProp(it, "providerId");
                        if (pid > 0) withPrice.Add(pid);
                    }
                    if ((long)(page + 1) * PS >= GetIntProp(d, "total")) break;
                }

                var stats = new
                {
                    total,
                    missingEmail,
                    missingPhone,
                    missingPrice = Math.Max(0, total - withPrice.Count),
                    withPrice = withPrice.Count
                };
                _nccStatsCache[sess.TenantId] = (stats, DateTime.UtcNow.Add(NccStatsTtl));
                return Results.Json(stats);
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "[ncc/stats] fail"); return Results.Json(new { error = "Không tính được thống kê NCC: " + ex.Message }, statusCode: 502); }
        });

        // ─── Thị trường THẬT (proxy TourKit /api/tours/markets, cache 6h per-tenant) ──
        // Tour-builder + Wizard dùng để fill dropdown Thị trường thay vì hardcode 12 string.
        v1.MapGet("/markets", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<EndpointsLog> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();

            var key = sess.TenantId;
            if (_marketsCache.TryGetValue(key, out var entry) && entry.Exp > DateTime.UtcNow)
                return Results.Json(entry.Names);

            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                var data = await api.GetAsync(jwt, "/api/tours/markets", ctx.RequestAborted);

                var names = new List<string>();
                if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in data.EnumerateArray())
                    {
                        if (it.ValueKind == JsonValueKind.Object &&
                            it.TryGetProperty("name", out var n) &&
                            n.ValueKind == JsonValueKind.String)
                        {
                            var name = n.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                        }
                    }
                }
                _marketsCache[key] = (names, DateTime.UtcNow.Add(MarketsTtl));
                return Results.Json(names);
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("[markets] upstream {Status}: {Msg}", ex.Status, ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[markets] fail");
                return Results.Json(new { error = "Không lấy được thị trường: " + ex.Message }, statusCode: 502);
            }
        });

        // ─── Permissions của user hiện tại ──────────────────────────────────────
        // Proxy `/api/auth/permissions` upstream → trả list mã quyền (CH_HT_XEM, NC_NC_XEM, …).
        // Frontend cache 1 lần sau login → filter nav "Tích hợp" + gate các page /widget-admin,
        // /visa-config, /workflows theo CH_HT_XEM (mirror web CRM). Không cache server-side vì
        // upstream đã cache theo tenant + response nhẹ (~vài KB).
        v1.MapGet("/permissions", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<EndpointsLog> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                JsonElement data;
                try { data = await api.GetAsync(jwt, "/api/auth/permissions", ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid!, ctx.RequestAborted);
                    data = await api.GetAsync(jwt, "/api/auth/permissions", ctx.RequestAborted);
                }
                return Results.Json(data);
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("[permissions] upstream {Status}: {Msg}", ex.Status, ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[permissions] fail");
                return Results.Json(new { error = "Không lấy được quyền: " + ex.Message }, statusCode: 502);
            }
        });

        return routes;
    }

    private static async Task<IResult> Proxy(Func<Task<JsonElement>> call)
    {
        try { return Results.Json(await call()); }
        catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        catch (Exception ex) { return Results.Json(new { error = "NCC lỗi: " + ex.Message }, statusCode: 502); }
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized() => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

    private static string? GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static JsonElement Clone(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.Clone() : default;

    // ── Helpers cho /tours/{id}/save-crm ────────────────────────────────────────────────────
    private static decimal Dec(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;

    /// Bỏ ký tự CRM chặn (UIHelper.SepecialCharacterRegex bên web) + gọn khoảng trắng. Tên tour do AI
    /// sinh hay có dấu nháy/chấm than → gửi thẳng là ăn 400 "không được chứa ký tự đặc biệt".
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = new string(s.Where(c => !"!@#$%^*?<>|\"'".Contains(c)).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// Phương tiện cho tour FIT (cột bắt buộc): 1 = đường bộ · 2 = đường thủy · 3 = hàng không.
    /// Đoán từ các hoạt động TRANSPORT trong lịch trình; không thấy gì thì mặc định đường bộ.
    private static byte GuessTransportation(JsonElement itinerary)
    {
        if (itinerary.ValueKind != JsonValueKind.Array) return 1;
        var text = new System.Text.StringBuilder();
        foreach (var day in itinerary.EnumerateArray())
        {
            if (day.ValueKind != JsonValueKind.Object || !day.TryGetProperty("activities", out var acts)
                || acts.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in acts.EnumerateArray())
                if (string.Equals(GetStr(a, "type"), "TRANSPORT", StringComparison.OrdinalIgnoreCase))
                    text.Append(GetStr(a, "title")).Append(' ');
        }
        var t = text.ToString().ToLowerInvariant();
        if (t.Contains("máy bay") || t.Contains("bay ") || t.Contains("hàng không")) return 3;
        if (t.Contains("tàu thủy") || t.Contains("du thuyền") || t.Contains("cano") || t.Contains("ca nô")) return 2;
        return 1;
    }

    // Helpers cho /ncc/stats — envelope AI surface camelCase (total/items/email/phone/providerId).
    private static int GetIntProp(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
    private static List<JsonElement> GetArrayItems(JsonElement e, string name)
    {
        var list = new List<JsonElement>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var it in a.EnumerateArray()) list.Add(it);
        return list;
    }
    private static bool IsBlankProp(JsonElement e, string name)
        => !(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
             && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()));
}
