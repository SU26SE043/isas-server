using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// DB25b — chạy một khối có transaction TỰ MỞ dưới <see cref="IExecutionStrategy"/> của EF.
///
/// <para>VÌ SAO BẮT BUỘC: <c>Program.cs</c> bật <c>EnableRetryOnFailure()</c> trên Npgsql, mà chiến
/// lược retry của EF TỪ CHỐI transaction do người dùng tự mở — gọi thẳng
/// <c>BeginTransactionAsync</c> sẽ ném <see cref="InvalidOperationException"/> ở MỌI request trên
/// Postgres, trong khi test SQLite (chiến lược không-retry) vẫn xanh 100%. Đúng lớp bug chỉ nổ trên
/// production mà CI không bắt được.</para>
///
/// <para>⚠ VÌ SAO PHẢI DỌN CHANGE TRACKER: khi retry chạy lại delegate, EF <b>KHÔNG</b> tự reset
/// trạng thái tracking. Hai khối dùng helper này đều <c>Add()</c> entity BÊN TRONG transaction
/// (<c>outbox_messages</c> ở sweeper · bản prompt mới ở registry):
/// <list type="bullet">
/// <item>Nếu <c>SaveChanges</c> hỏng tạm thời → entity kẹt ở <c>Added</c>; lần thử sau tạo entity MỚI
/// rồi <c>Add</c> tiếp ⇒ <c>SaveChanges</c> chèn CẢ HAI ⇒ hai outbox-row cho cùng một sự kiện (Payment
/// hấp thụ được nhờ PAY-11, nhưng vẫn là rác), hoặc HAI bản prompt cùng version.</item>
/// <item>Nếu <c>SaveChanges</c> XONG mà <c>Commit</c> mới hỏng → entity đã thành <c>Unchanged</c> nên
/// lần thử sau EF tưởng đã lưu và <b>KHÔNG chèn lại</b>, dù transaction đã rollback ⇒ session đóng
/// <c>SessionAbandoned</c> mà KHÔNG có outbox-row ⇒ credit không bao giờ được hoàn (đúng hình dạng
/// lỗi R1).</item>
/// </list>
/// Dọn tracker ở đầu mỗi lần thử LẠI biến delegate thành thật sự chạy-lại-từ-đầu, đóng cả hai ngả.</para>
///
/// <para>Lần thử ĐẦU cố ý KHÔNG dọn: giữ hành vi hôm nay byte-identical (retry vốn chưa từng chạy),
/// nên bản vá này không thể làm rơi thay đổi nào mà caller đang chờ lưu.</para>
/// </summary>
internal static class DbRetry
{
    /// <summary>Chạy <paramref name="work"/> (khối tự mở transaction) dưới execution strategy.</summary>
    public static Task<T> RunAsync<T>(DbContext db, Func<Task<T>> work)
    {
        var attempt = 0;
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            return await work();
        });
    }
}
