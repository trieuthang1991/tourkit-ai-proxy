/*
    Sửa các bản ghi providers.dataServices bị AI Import NCC ghi sai dạng OBJECT.

    Sai   : {"contactName":"Trần Minh Quân","contactPhone":"0236 3888 999"}
    Đúng  : [{"_name_member":"Trần Minh Quân","_position_member":"","_birthday_member":"","_phone_member":"0236 3888 999","_email_member":""}]

    Vì sao phải sửa: web đọc cột này bằng JsonConvert.DeserializeObject<List<dataServices>>()
    và KHÔNG bắt lỗi ở ProviderAction (thêm/sửa/xoá thẻ HDV, xuất giấy giới thiệu, dashboard
    đếm thẻ) → gặp object là ném JsonSerializationException, hỏng chức năng.

    Nguồn ghi sai đã vá ở tourkit-ai-proxy/Services/NccImport/NccQuoteMapper.cs (BuildContactJson).
    Script này chỉ dọn dữ liệu CŨ, chạy TAY một lần trên từng database tenant.

    Yêu cầu: SQL Server 2016+ (JSON_VALUE / STRING_ESCAPE / ISJSON).
    CHẠY THEO THỨ TỰ: (1) xem trước → (2) sao lưu → (3) update → (4) đối chiếu lại.
*/

-------------------------------------------------------------------------------
-- (1) XEM TRƯỚC — chạy riêng, kiểm bằng mắt trước khi update
-------------------------------------------------------------------------------
SELECT  id,
        provider_code,
        provider_name,
        dataServices                                   AS truoc_khi_sua,
        N'[{"_name_member":"'  + STRING_ESCAPE(ISNULL(JSON_VALUE(dataServices, '$.contactName'),  N''), 'json')
      + N'","_position_member":"","_birthday_member":"","_phone_member":"'
                               + STRING_ESCAPE(ISNULL(JSON_VALUE(dataServices, '$.contactPhone'), N''), 'json')
      + N'","_email_member":""}]'                      AS sau_khi_sua
FROM    providers
WHERE   dataServices IS NOT NULL
  AND   ISJSON(dataServices) = 1
  AND   LEFT(LTRIM(dataServices), 1) = '{'             -- object, không phải array
  AND   (JSON_VALUE(dataServices, '$.contactName') IS NOT NULL
      OR JSON_VALUE(dataServices, '$.contactPhone') IS NOT NULL);

-------------------------------------------------------------------------------
-- (2) SAO LƯU giá trị cũ — giữ lại để hoàn tác nếu cần
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.providers_dataServices_backup_20260825') IS NULL
    SELECT  id, dataServices, SYSUTCDATETIME() AS backup_at_utc
    INTO    dbo.providers_dataServices_backup_20260825
    FROM    providers
    WHERE   dataServices IS NOT NULL
      AND   ISJSON(dataServices) = 1
      AND   LEFT(LTRIM(dataServices), 1) = '{'
      AND   (JSON_VALUE(dataServices, '$.contactName') IS NOT NULL
          OR JSON_VALUE(dataServices, '$.contactPhone') IS NOT NULL);

-------------------------------------------------------------------------------
-- (3) SỬA — chỉ đụng đúng các dòng dạng object có contactName/contactPhone
-------------------------------------------------------------------------------
UPDATE  providers
SET     dataServices =
            N'[{"_name_member":"'  + STRING_ESCAPE(ISNULL(JSON_VALUE(dataServices, '$.contactName'),  N''), 'json')
          + N'","_position_member":"","_birthday_member":"","_phone_member":"'
                                   + STRING_ESCAPE(ISNULL(JSON_VALUE(dataServices, '$.contactPhone'), N''), 'json')
          + N'","_email_member":""}]'
WHERE   dataServices IS NOT NULL
  AND   ISJSON(dataServices) = 1
  AND   LEFT(LTRIM(dataServices), 1) = '{'
  AND   (JSON_VALUE(dataServices, '$.contactName') IS NOT NULL
      OR JSON_VALUE(dataServices, '$.contactPhone') IS NOT NULL);

-------------------------------------------------------------------------------
-- (4) ĐỐI CHIẾU — phải trả về 0 dòng
-------------------------------------------------------------------------------
SELECT  COUNT(*) AS con_sot_dang_object
FROM    providers
WHERE   dataServices IS NOT NULL
  AND   ISJSON(dataServices) = 1
  AND   LEFT(LTRIM(dataServices), 1) = '{';

/*  HOÀN TÁC (nếu cần):
    UPDATE p SET p.dataServices = b.dataServices
    FROM providers p JOIN dbo.providers_dataServices_backup_20260825 b ON b.id = p.id;
*/
