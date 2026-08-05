using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;
 
    public Guid QuestionId { get; set; }
    public PracticeQuestion Question { get; set; } = null!;
 
    // Con trỏ tới SeaweedFS (key/path, không phải URL công khai)
    public string? AudioObjectKey { get; set; }
 
    public string? Transcript { get; set; }

    /// <summary>
    /// Engine đã CHÉP ra <see cref="Transcript"/> ở trên (vd <c>whisper-1</c>, <c>gemini-2.5-flash</c>,
    /// <c>whisper-local-small</c>). Nguồn: AIService gửi kèm — đường thích ứng qua <c>/decide-next</c>,
    /// đường tĩnh qua callback chấm.
    ///
    /// <para><b>Vì sao cần:</b> AIService chép qua nhà cung cấp TỪ XA và rơi về Whisper CỤC BỘ khi
    /// mạng hỏng, nên <b>hai answer trong CÙNG một buổi có thể được chép bằng hai engine khác nhau</b>.
    /// Chất lượng chữ đi thẳng vào điểm chấm: đo thật thì Whisper <c>small</c> sai 4,2% số từ (chép
    /// "người dùng <b>cần</b> thiết" thành "người dùng <b>tầng</b> thiết") trong khi engine từ xa sai
    /// 0,5–0,7%. Điểm sinh ra từ hai bản chép đó vẫn bị đem so với nhau ở xếp hạng B2B (CAMP-10) và
    /// ở phần đo cải thiện của roadmap (BC15) — không đánh dấu thì chênh lệch đó vô hình.</para>
    ///
    /// <para>Reset cùng transcript khi upload lại (INT-3): giữ lại là hiện lai lịch của một bản ghi
    /// âm không còn tồn tại — cùng lý do với <see cref="SampleAnswer"/> và cụm chỉ số F11.</para>
    ///
    /// <para>⚠ <c>null</c> = <b>chép TRƯỚC bản vá này</b> (cột chỉ tồn tại từ đây trở đi, và mọi lượt
    /// chép từ đây đều đóng dấu ⇒ khuyết dấu chỉ có đúng một nguyên nhân). Suy luận đó NGƯỢC quy ước
    /// BK23 của <c>answer_scores.prompt_version</c>, nơi <c>null</c> bắt buộc giữ nghĩa "không biết"
    /// vì con dấu đến từ worker có thể lệch nhịp deploy bất kỳ lúc nào. Khác biệt nằm ở chỗ: ở đây
    /// engine là thuộc tính của CHÍNH bản chép đang lưu, và bản chép đó chỉ được ghi bởi code sau bản
    /// vá; ở BK23 con dấu mô tả một lượt chấm do bên khác thực hiện. <b>Đừng áp ngược tiền lệ này.</b>
    /// Ngoại lệ được cài tường minh trong <c>AnswerService.SaveResultAsync</c>: worker CŨ gửi transcript
    /// MỚI mà không kèm dấu ⇒ dấu về <c>null</c>, chứ không giữ dấu cũ (dấu nói dối tệ hơn khuyết dấu).</para>
    ///
    /// <para>Cột để kiểu <c>text</c> (không <c>HasMaxLength</c>) là CÓ CHỦ ĐÍCH: tên model do bên thứ ba
    /// đặt và dài tuỳ ý (<c>gemini-2.5-flash-preview-native-audio-dialog</c> đã 43 ký tự). Chọn
    /// <c>varchar(n)</c> ở đây là dựng lại đúng bẫy <c>credit_reservations.funded_by varchar(16)</c> —
    /// SQLite không enforce độ dài nên CI xanh 100% trong khi Postgres ném lúc chạy thật.</para>
    /// </summary>
    public string? TranscriptEngine { get; set; }

    public AnswerStatus Status { get; set; } = AnswerStatus.Uploaded;

    // E10 — self-consistency: chấm N lần, nếu spread điểm giữa các attempt (mỗi tiêu chí) vượt
    // Scoring:VarianceThreshold → gắn cờ này để HR (B2B) / người luyện (B2C) xem lại. Điểm AI
    // = gợi ý (INT-14/15/16), KHÔNG auto coi là điểm cuối. Mặc định false (N=1 → luôn false).
    public bool NeedsReview { get; set; }

    // F13 (FR07) — câu trả lời MẪU mức tối đa cho ĐÚNG câu hỏi này, do CÙNG lượt chấm sinh ra
    // (worker gửi kèm callback → không tốn thêm 1 call AI). null = chưa chấm / LLM không trả.
    // ⚠ KHÁC HẲN RubricLevel.ExampleAnswers: cái kia là anchor ĐẦU VÀO để hiệu chỉnh AI lúc chấm,
    // không bao giờ trả ra cho người dùng (và thực tế luôn rỗng vì không có write path nào).
    // Reset cùng transcript/scores khi upload lại (INT-3) — giữ lại sẽ hiện gợi ý của bài CŨ.
    public string? SampleAnswer { get; set; }

    public int DurationSec { get; set; }

    // ── F11 (FR06) — chỉ số ĐỘ TRÔI CHẢY đo từ audio (mốc thời gian: VAD từ 2026-08-05) ─────
    // Tất cả nullable: null = CHƯA ĐO ĐƯỢC (answer cũ trước F11 · audio rỗng · đường degrade khi
    // /decide-next lỗi), KHÁC HẲN với 0 = "đo ra 0". Phân biệt được hai ca này mới hiển thị đúng:
    // "chưa có dữ liệu" vs "nói 0 từ đệm". Reset cùng transcript/scores khi upload lại (INT-3) —
    // giữ lại là hiện chỉ số của bản ghi âm không còn tồn tại.
    //
    // ⚠ FillerCount là mức TỐI THIỂU (Whisper nuốt bớt từ đệm) — xem DeliveryMetricsDto.

    /// <summary>Âm tiết/phút (tiếng Việt đơn âm tiết).</summary>
    public double? SpeechRateWpm { get; set; }

    public int? FillerCount { get; set; }
    public int? PauseCount { get; set; }
    public double? LongestPauseSec { get; set; }
    public double? SilenceRatio { get; set; }

    // ── Vá F11 (2026-07-19) — 4 cột BỔ SUNG ────────────────────────────────────────────────
    // Trước bản vá, DTO khai 9 field nhưng chỉ 5 cột được lưu ⇒ `DeliveryMetricsMapper.Read()`
    // dựng lại DTO với audioSec/speechSec/wordCount/fillerPer100Words = 0.
    //
    // Đó KHÔNG chỉ là lỗi hiển thị: CẢ HAI đường đẩy job chấm đều đi qua `Read()`
    // (`AnswerService` đường thích ứng · `StuckAnswerRepublisher` đường cứu), nên prompt chấm
    // nhận "nói trong 0s / tổng 0s audio" và "0 lần/100 âm tiết" — trong khi
    // `build_delivery_block` giới thiệu chính khối đó là "số liệu thật" và dặn LLM coi chỉ số
    // THỜI GIAN là bằng chứng ĐÁNG TIN NHẤT. Tức là vừa bịa số vừa bảo mô hình hãy tin nó nhất,
    // và số bịa ("0 từ đệm/100 âm tiết") nghiêng về phía KHEN người luyện.
    //
    // Nullable như 5 cột trên: null = chưa đo được, KHÁC 0 = đo ra 0.

    /// <summary>Tổng độ dài audio (giây) Whisper báo.</summary>
    public double? AudioSec { get; set; }

    /// <summary>Thời lượng THỰC SỰ có tiếng nói (giây) = tổng audio trừ khoảng lặng.</summary>
    public double? SpeechSec { get; set; }

    /// <summary>Số âm tiết đếm được trong transcript (mẫu số của <see cref="FillerPer100Words"/>).</summary>
    public int? WordCount { get; set; }

    /// <summary>Từ đệm trên 100 âm tiết. Lưu thay vì tính lại từ <see cref="FillerCount"/> và
    /// <see cref="WordCount"/> để con số hiển thị cho người dùng TRÙNG KHÍT con số đã đưa vào
    /// prompt chấm — tính lại ở hai nơi là hai cơ hội lệch nhau.</summary>
    public double? FillerPer100Words { get; set; }

    /// <summary>Chi tiết từ đệm dạng JSON (<c>{"ừm":3,"kiểu như":1}</c>) để hiện cho người luyện.
    /// Lưu <b>text</b> chứ không phải jsonb: dữ liệu này chỉ để đọc-hiển-thị, không truy vấn theo
    /// khoá bao giờ — chọn jsonb ở đây chỉ tổ rước rủi ro migration (xem F15) mà không được gì.</summary>
    public string? FillerBreakdown { get; set; }

    /// <summary>
    /// Phiên bản THƯỚC ĐO đã sinh ra các cột trên (AIService <c>fluency.DELIVERY_METRICS_VERSION</c>).
    /// <c>1</c> = mốc thời gian từ biên segment Whisper · <c>2</c> = từ vùng tiếng nói VAD.
    ///
    /// <para>Vì sao cần: bản vá 2026-08-05 đổi cách đo, và số cũ với số mới KHÔNG so sánh được.
    /// Trên 7 ghi âm thật, thước cũ bắt được 2/21 khoảng lặng — một câu trả lời 45s ngập ngừng
    /// 7 lần bị ghi <c>PauseCount = 0</c>, <c>SilenceRatio = 0,020</c> (thực tế 0,315). Điểm chấm
    /// từ hai thước đó vẫn bị đem so với nhau ở xếp hạng B2B (CAMP-10) và ở phần đo cải thiện
    /// của roadmap (BC15) nếu không có dấu để phân biệt.</para>
    ///
    /// <para>⚠ <c>null</c> = đo bằng thước CŨ. Suy luận này an toàn ở đây (cột chỉ tồn tại từ
    /// bản vá trở đi, và mọi lượt đo từ đó đều đóng dấu) nhưng NGƯỢC quy ước BK23 của
    /// <c>prompt_version</c>, nơi <c>null</c> phải giữ nghĩa "không biết". Đừng áp ngược.</para>
    /// </summary>
    public int? MetricsVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Mốc lần gần nhất job chấm được đẩy lên queue (kể cả republish).
    // null = chưa publish lần nào (publish hụt lúc upload).
    // Republisher đo elapsed theo cột này -> biết answer đang chờ hay đã kẹt thật,
    // tránh đẩy lại answer mà worker vẫn đang chấm (Whisper CPU chậm).
    public DateTime? LastScoringPublishedAt { get; set; }

    // Navigation
    public ICollection<AnswerScore> Scores { get; set; } = [];
}