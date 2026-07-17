namespace Isas.InterviewService.Entities;

/// <summary>
/// DB14 — marker cho entity có cột <c>updated_at</c> được đóng dấu TỰ ĐỘNG mỗi khi bản ghi bị sửa.
/// <c>InterviewDbContext</c> override <c>SaveChanges</c>/<c>SaveChangesAsync</c> quét
/// <c>ChangeTracker.Entries&lt;IHasUpdatedAt&gt;()</c> ở trạng thái
/// <see cref="Microsoft.EntityFrameworkCore.EntityState.Modified"/> → set <c>UpdatedAt = DateTime.UtcNow</c>.
/// LƯU Ý: cập nhật set-based qua <c>ExecuteUpdateAsync</c> KHÔNG đi qua SaveChanges → phải tự thêm
/// <c>.SetProperty(x =&gt; x.UpdatedAt, _ =&gt; DateTime.UtcNow)</c> tại mỗi call flip trên practice_sessions.
/// </summary>
public interface IHasUpdatedAt
{
    DateTime UpdatedAt { get; set; }
}
