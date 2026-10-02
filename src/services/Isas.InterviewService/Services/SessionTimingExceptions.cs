namespace Isas.InterviewService.Services;

/// <summary>
/// ATT1 — lỗi 409 CÓ MÃ của đồng hồ cả buổi. Controller trả <c>409 { code, error }</c>.
///
/// <para>⚠ CỐ Ý KHÔNG kế thừa <see cref="InvalidOperationException"/>: các controller đang có
/// <c>catch (InvalidOperationException)</c> trả 409/400 KHÔNG kèm <c>code</c> (AnswersController) —
/// kế thừa nó thì một catch cha đặt nhầm thứ tự sẽ nuốt lỗi và client mất mã để phân biệt "chưa vào
/// phòng" với "hết giờ". Và ở đường speech, PracticeController không bắt InvalidOperationException
/// ⇒ ném loại đó ở đó là 500.</para>
/// </summary>
public abstract class SessionTimingConflictException : Exception
{
    public string Code { get; }

    protected SessionTimingConflictException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>Buổi tính giờ chưa vào phòng (chưa begin) ⇒ chưa được nộp / nghe câu.</summary>
public sealed class SessionNotBegunException()
    : SessionTimingConflictException("SESSION_NOT_BEGUN",
        "Bạn chưa vào phòng thi. Hãy bấm vào phòng để bắt đầu tính giờ trước khi trả lời.");

/// <summary>Quá hạn chót của buổi + ân hạn ⇒ server ngừng nhận câu trả lời.</summary>
public sealed class SessionTimeUpException()
    : SessionTimingConflictException("SESSION_TIME_UP",
        "Đã hết thời gian làm bài. Câu trả lời này không được nhận.");

/// <summary>Begin một buổi đã kết thúc (Scoring/Scored/Completed/SessionAbandoned/Failed).</summary>
public sealed class SessionEndedException()
    : SessionTimingConflictException("SESSION_ENDED",
        "Buổi phỏng vấn đã kết thúc, không thể vào phòng.");
