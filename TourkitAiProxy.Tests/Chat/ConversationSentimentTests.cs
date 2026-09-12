using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Thang cảm xúc 5 bậc (chủ dự án đặt 12/09/2026).
///
/// <para>Đây là luật NGHIỆP VỤ chứ không phải tiện ích: một khách đang giận bị chấm nhầm thành
/// bình thường là một cuộc gọi khiếu nại không ai gọi. Nên bài kiểm bám vào hành vi người dùng
/// thấy được, không bám vào chi tiết cài đặt.</para>
/// </summary>
public class ConversationSentimentTests
{
    [Fact]
    public void Scale_has_five_levels_ordered_worst_to_best()
    {
        Assert.Equal(5, ConversationSentiment.Levels.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal((short)(i + 1), (short)ConversationSentiment.Levels[i].Level);

        // Mỗi bậc phải có đủ chữ để hiện ra màn hình — thiếu một ô là màn hình trống một chỗ.
        foreach (var m in ConversationSentiment.Levels)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Label));
            Assert.False(string.IsNullOrWhiteSpace(m.Icon));
            Assert.False(string.IsNullOrWhiteSpace(m.Signals));
            Assert.False(string.IsNullOrWhiteSpace(m.Action));
        }
    }

    [Theory]
    [InlineData("🤬", SentimentLevel.VeryNegative)]
    [InlineData("😡", SentimentLevel.VeryNegative)]
    [InlineData("👎", SentimentLevel.Negative)]
    [InlineData("😢", SentimentLevel.Negative)]
    [InlineData("😐", SentimentLevel.Neutral)]
    [InlineData("👍", SentimentLevel.Positive)]
    [InlineData("😍", SentimentLevel.VeryPositive)]
    public void Common_emoji_score_to_the_right_level(string emoji, SentimentLevel expected)
        => Assert.Equal(expected, ConversationSentiment.ScoreEmoji(emoji));

    [Fact]
    public void Heart_WITH_variation_selector_still_scores()
    {
        // Meta gửi "❤️" = ❤ + U+FE0F, còn bảng tra ghi "❤". Hai chuỗi hiện lên màn hình y HỆT
        // nhau nên mắt không bao giờ bắt được, mà không cắt FE0F thì mọi lượt thả tim trên
        // Messenger đều rơi vào "không nhận ra".
        Assert.Equal(SentimentLevel.VeryPositive, ConversationSentiment.ScoreEmoji("❤️"));
        Assert.Equal(SentimentLevel.VeryPositive, ConversationSentiment.ScoreEmoji("❤"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("🦕")]      // biểu tượng thật, nhưng không nói gì về cảm xúc
    [InlineData("abc")]
    public void Unknown_input_returns_NULL_not_neutral(string? emoji)
        => Assert.Null(ConversationSentiment.ScoreEmoji(emoji));

    // ── Cảm xúc thả: PHẢI phân biệt nền tảng ────────────────────────────────

    [Theory]
    [InlineData("angry", SentimentLevel.VeryNegative)]
    [InlineData("sad", SentimentLevel.Negative)]
    [InlineData("like", SentimentLevel.Positive)]
    [InlineData("love", SentimentLevel.VeryPositive)]
    public void Meta_scores_by_its_own_reaction_NAME(string name, SentimentLevel expected)
    {
        // Tên do Meta định nghĩa: tập đóng, không đổi theo phiên bản ứng dụng, không dính bộ chọn
        // biến thể. Đáng tin hơn hẳn biểu tượng.
        Assert.Equal(expected, ConversationSentiment.ScoreReaction(ChatChannel.Messenger, null, name));
        Assert.Equal(expected, ConversationSentiment.ScoreReaction(ChatChannel.Instagram, null, name));
    }

    [Fact]
    public void Meta_falls_back_to_the_emoji_when_the_name_is_unknown()
    {
        // Meta thêm cảm xúc mới mà mình chưa cập nhật bảng tên → vẫn còn biểu tượng để dựa vào.
        Assert.Equal(SentimentLevel.VeryPositive,
            ConversationSentiment.ScoreReaction(ChatChannel.Messenger, "😍", "reaction_moi_cua_meta"));
    }

    [Fact]
    public void Telegram_NEVER_reads_the_name_because_it_is_a_custom_emoji_id()
    {
        // ĐÂY LÀ BÀI QUAN TRỌNG NHẤT CỦA CẢ LỚP, và là lỗi chủ dự án bắt được ngày 12/09/2026.
        //
        // Trường Name mang hai thứ HOÀN TOÀN khác nhau tuỳ kênh: với Meta là tên cảm xúc
        // ("love"), với Telegram là custom_emoji_id — một dãy số định danh biểu tượng riêng của
        // người dùng Premium. Chấm theo Name mà không nhìn kênh thì một mã số bất kỳ có thể trùng
        // một chữ trong bảng và chấm sai, không ai hiểu tại sao.
        //
        // "love" ở đây đóng vai một custom_emoji_id xui xẻo trùng chữ. Telegram PHẢI bỏ qua nó.
        Assert.Null(ConversationSentiment.ScoreReaction(ChatChannel.Telegram, null, "love"));
        Assert.Null(ConversationSentiment.ScoreReaction(ChatChannel.Telegram, null, "angry"));

        // Có biểu tượng chuẩn thì chấm bình thường.
        Assert.Equal(SentimentLevel.Positive,
            ConversationSentiment.ScoreReaction(ChatChannel.Telegram, "👍", "5123456789"));
    }

    [Fact]
    public void Telegram_premium_custom_emoji_scores_to_NULL()
        // Người ta dán một con mèo nhảy múa lên tin: không ai biết nó khen hay chê, kể cả người
        // thật. Đoán ở đây là bịa.
        => Assert.Null(ConversationSentiment.ScoreReaction(ChatChannel.Telegram, null, "5445284980978621387"));

    [Fact]
    public void Wow_and_thinking_stay_NEUTRAL_on_purpose()
    {
        // Ngạc nhiên không có dấu: khách có thể wow vì tour đẹp, cũng có thể wow vì giá gấp đôi
        // chỗ khác. Đoán sai hướng còn tệ hơn không đoán.
        Assert.Equal(SentimentLevel.Neutral,
            ConversationSentiment.ScoreReaction(ChatChannel.Messenger, "😮", "wow"));
        Assert.Equal(SentimentLevel.Neutral, ConversationSentiment.ScoreEmoji("🤔"));
    }

    // ── Biểu tượng khách GÕ trong tin ───────────────────────────────────────

    [Fact]
    public void Text_scoring_takes_the_STRONGEST_emoji_not_the_first()
    {
        // Xã giao trước, ý thật sau — cái sau mới là điều khách muốn nói.
        Assert.Equal(SentimentLevel.VeryNegative,
            ConversationSentiment.ScoreText("Cảm ơn bạn 😊 nhưng giá này thì 😡"));
        // Và ngược thứ tự vẫn ra đúng cái mạnh nhất.
        Assert.Equal(SentimentLevel.VeryPositive,
            ConversationSentiment.ScoreText("Tuyệt quá 😍 hơi lâu tí 🙂"));
    }

    [Fact]
    public void A_tie_leans_to_the_WORSE_side()
    {
        // 😡 và 😍 cùng cách bậc 3 đúng 2 nấc. Bỏ sót khách đang giận đắt hơn nhiều so với chăm
        // hơi kỹ một khách đang vui — nên hoà thì lấy bậc thấp.
        Assert.Equal(SentimentLevel.VeryNegative, ConversationSentiment.ScoreText("😡😍"));
        Assert.Equal(SentimentLevel.VeryNegative, ConversationSentiment.ScoreText("😍😡"));
    }

    [Fact]
    public void Plain_text_with_no_emoji_returns_NULL()
        => Assert.Null(ConversationSentiment.ScoreText("Cho mình hỏi tour Nhật tháng 10 còn chỗ không"));

    [Fact]
    public void Emoji_between_Vietnamese_words_is_still_found()
        // Duyệt theo cụm ký tự chứ không theo char: duyệt theo char thì biểu tượng vỡ làm đôi.
        // Chữ tiếng Việt có dấu cũng là cụm nhiều mã, nên nó canh luôn cả vế đó.
        => Assert.Equal(SentimentLevel.VeryPositive,
            ConversationSentiment.ScoreText("Chị đặt luôn tour này nhé 🥰 em gửi giá giúp chị"));

    // ── Thống kê cả cuộc hội thoại ──────────────────────────────────────────

    [Fact]
    public void Hoi_thoai_TOAN_TICH_CUC_thi_ket_qua_phai_tich_cuc()
    {
        // Đúng câu chủ dự án nêu 12/09/2026: "trong cuộc hội thoại toàn tích cực thì cung bậc
        // cảm xúc phải happy".
        var sum = (int)SentimentLevel.Positive * 4 + (int)SentimentLevel.VeryPositive * 2;
        Assert.Equal(SentimentLevel.Positive, ConversationSentiment.Aggregate(sum, 6));
    }

    [Fact]
    public void MOT_tin_hieu_xau_KHONG_lat_do_ca_cuoc_tro_chuyen_tot()
    {
        // ĐÂY LÀ BÀI CHO LỖI CỦA BẢN ĐẦU: bản đó ghi ĐÈ mỗi lần có tín hiệu mới, nên khách khen
        // mười câu rồi lỡ thả một mặt buồn là cả hội thoại thành tiêu cực — chín tín hiệu tốt
        // trước đó biến mất không dấu vết.
        var sum = (int)SentimentLevel.VeryPositive * 10 + (int)SentimentLevel.Negative;
        var ra = ConversationSentiment.Aggregate(sum, 11);
        Assert.True(ra >= SentimentLevel.Positive, $"Một tín hiệu xấu đã kéo cả hội thoại xuống {ra}");
    }

    [Fact]
    public void Chua_co_tin_hieu_nao_tra_NULL_chu_khong_phai_trung_tinh()
        // Hội thoại chưa ai thả biểu tượng và hội thoại khách thật sự bình thản là hai chuyện.
        // Gộp lại thì một hộp thư toàn khách im lặng trông như ai cũng hài lòng.
        => Assert.Null(ConversationSentiment.Aggregate(0, 0));

    [Theory]
    [InlineData(5, 1, SentimentLevel.VeryPositive)]   // đúng một tín hiệu
    [InlineData(6, 2, SentimentLevel.Neutral)]        // trung bình 3
    [InlineData(3, 3, SentimentLevel.VeryNegative)]   // toàn bậc 1
    public void Trung_binh_duoc_lam_tron_ve_dung_bac(int sum, int count, SentimentLevel mong)
        => Assert.Equal(mong, ConversationSentiment.Aggregate(sum, count));

    [Fact]
    public void Hoa_giua_hai_bac_thi_lam_tron_ve_phia_XAU()
    {
        // Trung bình đúng 3,5 → bậc 3, không phải 4. Cùng lý do với luật hoà khi chấm chuỗi.
        // ⚠️ Math.Round mặc định làm tròn nửa về số CHẴN (3,5→4 nhưng 2,5→2) — vừa không đoán
        // được vừa sai hướng, nên hàm tự tính chứ không gọi nó.
        Assert.Equal(SentimentLevel.Neutral, ConversationSentiment.Aggregate(7, 2));    // 3,5
        Assert.Equal(SentimentLevel.Negative, ConversationSentiment.Aggregate(5, 2));   // 2,5
    }

    [Theory]
    [InlineData(SentimentLevel.VeryNegative, true)]
    [InlineData(SentimentLevel.Negative, true)]
    [InlineData(SentimentLevel.Neutral, false)]
    [InlineData(SentimentLevel.Positive, false)]
    [InlineData(SentimentLevel.VeryPositive, false)]
    public void Escalation_threshold_is_levels_1_and_2(SentimentLevel level, bool expected)
        => Assert.Equal(expected, ConversationSentiment.NeedsEscalation(level));
}
