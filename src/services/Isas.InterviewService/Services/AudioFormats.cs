namespace Isas.InterviewService.Services;

/// <summary>
/// Tín hiệu nào đã quyết định định dạng. Dùng để ghi log quan sát: nếu production xuất hiện nhiều lượt
/// KHÔNG phải <see cref="MagicBytes"/> thì có client đang gửi thứ ta chưa lường, biết trước khi nó thành sự cố.
/// </summary>
public enum AudioFormatSource
{
    /// <summary>Nội dung file — nguồn đáng tin nhất, client không tự khai được.</summary>
    MagicBytes,
    /// <summary>Content-Type client khai.</summary>
    ContentType,
    /// <summary>Đuôi tên file client đặt — yếu nhất.</summary>
    FileName,
}

/// <summary>
/// Nhận dạng định dạng audio câu trả lời và ánh xạ MIME ↔ đuôi file.
///
/// <para><b>Vì sao cần:</b> trước đây <c>AnswerService</c> lưu MỌI bản ghi âm với đuôi <c>.webm</c> cố định
/// và <c>PracticeService</c> phát lại với <c>audio/webm</c> cố định. Đúng với Chrome/Firefox, SAI với iOS
/// (<c>AVAudioRecorder</c> đẻ ra m4a/AAC) và Android. Hai hệ quả: phát lại vỡ trên iOS, và — nguy hiểm hơn vì
/// im lặng — đuôi của S3 key là thứ AIService dùng để suy MIME khi gửi file gốc lên OpenAI/Gemini
/// (<c>app/main.py</c>, <c>app/worker.py</c>: <c>suffix = os.path.splitext(key)[1] or ".webm"</c>).</para>
///
/// <para><b>Hợp đồng với AIService (đừng phá):</b> mọi đuôi ở đây PHẢI nằm trong <c>ORIGINAL_EXTENSIONS</c>
/// (<c>app/transcriber.py</c>) và có mặt trong <c>AUDIO_CONTENT_TYPES</c> (<c>app/transcribe_providers.py</c>).
/// Hợp đồng này được khoá bằng test phía Python — thêm đuôi ở đây mà quên bên kia thì test đỏ, không phải
/// phát hiện lúc chạy thật.</para>
///
/// <para><b>Cố ý KHÔNG nhận</b> <c>audio/aac</c>, <c>audio/3gpp</c>, <c>audio/amr</c>: chúng không nằm trong
/// <c>ORIGINAL_EXTENSIONS</c>. Map chúng sang <c>.m4a</c> cho "tiện" là nói dối về nội dung file — hôm nay chưa
/// cắn vì cờ <c>TRANSCRIBE_SEND_ORIGINAL</c> đang tắt (AIService decode bằng PyAV, probe nội dung), nhưng bật
/// cờ đó chỉ cần một biến env. Client mobile phải ghi ra container MPEG-4 (<c>.m4a</c>).</para>
/// </summary>
public static class AudioFormats
{
    /// <summary>Đuôi mặc định thời chưa có nhận dạng — giữ lại cho nhánh kill-switch (xem AnswersController).</summary>
    public const string LegacyExt = "webm";

    /// <summary>7 MIME chuẩn ↔ 7 đuôi. Đây là tập ĐÓNG: mọi thứ khác đi qua <see cref="Aliases"/> hoặc bị từ chối.</summary>
    private static readonly Dictionary<string, string> MimeToExt = new(StringComparer.Ordinal)
    {
        ["audio/webm"] = "webm",
        ["audio/ogg"] = "ogg",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "m4a",
        ["video/mp4"] = "mp4",
        ["audio/flac"] = "flac",
        ["audio/wav"] = "wav",
    };

    /// <summary>
    /// Cách viết khác của cùng một định dạng. Chỉ đi VÀO (chuẩn hoá về MIME chuẩn), không bao giờ đi ra —
    /// nhờ vậy thứ ta lưu lên S3 và trả cho client luôn là một trong 7 giá trị chuẩn.
    /// <c>audio/mp4a-latm</c> là hằng MIME của Android <c>MediaFormat</c>; app nào gửi MIME của codec thay vì
    /// của container sẽ rơi vào đây.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["video/webm"] = "audio/webm",
        ["application/ogg"] = "audio/ogg",
        ["audio/mp3"] = "audio/mpeg",
        ["audio/x-m4a"] = "audio/mp4",
        ["audio/m4a"] = "audio/mp4",
        ["audio/mp4a-latm"] = "audio/mp4",
        ["audio/x-flac"] = "audio/flac",
        ["audio/x-wav"] = "audio/wav",
        ["audio/wave"] = "audio/wav",
        ["audio/vnd.wave"] = "audio/wav",
    };

    private static readonly Dictionary<string, string> ExtToMime =
        MimeToExt.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Danh sách định dạng chấp nhận, để đưa vào thông điệp 400 cho client biết đường sửa.</summary>
    public static string AcceptedList => string.Join(", ", MimeToExt.Keys);

    /// <summary>
    /// Nhận dạng định dạng thật của file theo thứ tự: <b>magic bytes → Content-Type → đuôi tên file</b>.
    ///
    /// <para>Magic bytes đi TRƯỚC vì nó là tín hiệu duy nhất client không tự khai được. Hai nguồn kia đều
    /// nói dối được, và trên thực tế đang nói dối: FE web hardcode tên file <c>answer.webm</c> ở mọi call-site
    /// (<c>practice.api.ts</c>) kể cả trên Safari nơi ruột là mp4. Đặt tên file lên trước thì nó sẽ trả lời
    /// sai một cách tự tin.</para>
    ///
    /// <para>Đồng thời đây là lá chắn chống 400 oan: file audio thật luôn có magic bytes hợp lệ, nên nhánh
    /// từ chối chỉ chạm được khi bytes không phải bất kỳ định dạng nào ta nhận.</para>
    /// </summary>
    /// <param name="head">Vài byte đầu của file (cần ≥ 12 byte để phân biệt WAV).</param>
    /// <param name="canonicalMime">MIME chuẩn — một trong 7 giá trị của <see cref="MimeToExt"/>.</param>
    /// <param name="ext">Đuôi tương ứng, KHÔNG kèm dấu chấm.</param>
    public static bool TryResolve(
        ReadOnlySpan<byte> head, string? contentType, string? fileName,
        out string canonicalMime, out string ext, out AudioFormatSource source)
    {
        // (0) Nội dung file — nguồn không do client điều khiển.
        var sniffed = SniffMagicBytes(head, contentType);
        if (sniffed is not null)
        {
            canonicalMime = sniffed;
            ext = MimeToExt[sniffed];
            source = AudioFormatSource.MagicBytes;
            return true;
        }

        // (a) Content-Type client khai.
        if (TryNormalizeMime(contentType, out canonicalMime))
        {
            ext = MimeToExt[canonicalMime];
            source = AudioFormatSource.ContentType;
            return true;
        }

        // (b) Đuôi tên file — nấc cuối, để client không set được Content-Type (một số HTTP client mobile)
        //     vẫn upload được. Ở web đây là lưới mù (xem chú thích trên), nên nó nằm SAU magic bytes.
        var dot = fileName?.LastIndexOf('.') ?? -1;
        if (dot >= 0 && ExtToMime.TryGetValue(fileName![(dot + 1)..].Trim(), out var mimeFromExt))
        {
            canonicalMime = mimeFromExt;
            ext = MimeToExt[mimeFromExt];
            source = AudioFormatSource.FileName;
            return true;
        }

        canonicalMime = string.Empty;
        ext = string.Empty;
        source = AudioFormatSource.FileName;
        return false;
    }

    /// <summary>
    /// Đuôi file cho một MIME đã chuẩn hoá. Không nhận ra → <see cref="LegacyExt"/> (hành vi trước bản vá):
    /// đường duy nhất tới đây với MIME lạ là khi kill-switch tắt cổng kiểm ở controller.
    ///
    /// <para>⚠ Giá trị trả về chảy thẳng vào <c>StorageService.BuildKey</c>, nơi KHÔNG sanitize gì ngoài
    /// <c>TrimStart('.').ToLower()</c>. Nên nó phải LUÔN là hằng lấy từ bảng — đừng bao giờ "tối ưu" thành
    /// <c>Path.GetExtension(file.FileName)</c>, đó là chuỗi do client đặt.</para>
    /// </summary>
    public static string ExtFor(string? canonicalMime) =>
        TryNormalizeMime(canonicalMime, out var mime) ? MimeToExt[mime] : LegacyExt;

    /// <summary>
    /// MIME để phát lại, suy từ đuôi của S3 object key. Đuôi lạ (dữ liệu cũ, hoặc key do đường khác tạo)
    /// → <c>application/octet-stream</c>: nói "không biết" đúng hơn là khẳng định sai một định dạng cụ thể.
    /// </summary>
    public static string ContentTypeForKey(string? objectKey)
    {
        var dot = objectKey?.LastIndexOf('.') ?? -1;
        return dot >= 0 && ExtToMime.TryGetValue(objectKey![(dot + 1)..].Trim(), out var mime)
            ? mime
            : "application/octet-stream";
    }

    /// <summary>
    /// Chuẩn hoá Content-Type: bỏ tham số sau <c>;</c>, cắt khoảng trắng, hạ chữ thường, rồi tra bảng chuẩn
    /// và bảng alias.
    ///
    /// <para><c>Trim()</c> KHÔNG phải để xử lý <c>audio/ogg; codecs=opus</c> của Firefox — khoảng trắng ở đó
    /// nằm sau dấu chấm phẩy nên đã rơi vào phần tử thứ hai, <c>Split(';')[0]</c> vốn đã sạch. Nó phòng ca
    /// client bọc khoảng trắng quanh chính media-type (<c>" audio/mp4 "</c>), rẻ và có test khoá riêng.</para>
    /// </summary>
    private static bool TryNormalizeMime(string? contentType, out string canonicalMime)
    {
        canonicalMime = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var media = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (MimeToExt.ContainsKey(media))
        {
            canonicalMime = media;
            return true;
        }
        if (Aliases.TryGetValue(media, out var aliased))
        {
            canonicalMime = aliased;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Dò chữ ký nhị phân ở đầu file. Trả null khi không nhận ra (không phải lỗi — caller còn hai nguồn nữa).
    /// </summary>
    private static string? SniffMagicBytes(ReadOnlySpan<byte> head, string? contentType)
    {
        if (head.Length >= 4)
        {
            // Matroska/WebM — EBML header.
            if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
                return "audio/webm";
            if (Matches(head, 0, "OggS"))
                return "audio/ogg";
            if (Matches(head, 0, "fLaC"))
                return "audio/flac";
            if (Matches(head, 0, "ID3"))
                return "audio/mpeg";
            // MP3 không có magic cố định khi thiếu thẻ ID3 — dò frame sync 11 bit.
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
                return "audio/mpeg";
        }

        // ISO-BMFF (mp4/m4a): 4 byte đầu là kích thước box, 'ftyp' nằm ở offset 4.
        if (head.Length >= 8 && Matches(head, 4, "ftyp"))
        {
            // Cùng một container phục vụ cả hai. Chỉ Content-Type mới phân biệt được ý định của client,
            // nên ở ĐÚNG ca này ta để nó lên tiếng; mặc định là m4a vì đó là thứ điện thoại thu ra.
            return TryNormalizeMime(contentType, out var mime) && mime == "video/mp4"
                ? "video/mp4"
                : "audio/mp4";
        }

        // WAV: "RIFF" .... "WAVE" — phải kiểm cả hai, vì RIFF còn là vỏ của AVI/WebP.
        if (head.Length >= 12 && Matches(head, 0, "RIFF") && Matches(head, 8, "WAVE"))
            return "audio/wav";

        return null;
    }

    private static bool Matches(ReadOnlySpan<byte> head, int offset, string ascii)
    {
        if (head.Length < offset + ascii.Length)
            return false;
        for (var i = 0; i < ascii.Length; i++)
        {
            if (head[offset + i] != (byte)ascii[i])
                return false;
        }
        return true;
    }
}
