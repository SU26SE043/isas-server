using System.Security.Cryptography;
using System.Text;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// Bộ chuẩn rubric B2C, đọc cho CampaignService dùng làm "bộ mặc định theo nghề" khi Employer không
/// muốn tự nghĩ ra tiêu chí (quyết định 3+4 của đợt này).
///
/// <para>GEN-1: internal, KHÔNG qua gateway, gác bằng <c>X-Internal-Token</c> (không JWT).</para>
///
/// <para><b>Vì sao Campaign KÉO qua HTTP chứ không đọc thẳng DB:</b> DB-per-service (GEN-2).
/// Campaign CHÉP nội dung về <c>campaign_criteria</c> của chính nó (quyết định 4) nên admin sửa bản
/// gốc về sau KHÔNG ảnh hưởng chiến dịch đang chạy — đúng thứ mà cơ chế phiên bản của đợt trước dựng
/// lên để chặn.</para>
/// </summary>
[ApiController]
public class InternalRubricsController(
    InterviewDbContext db,
    IConfiguration config,
    ILogger<InternalRubricsController> logger) : ControllerBase
{
    /// <summary>
    /// Bộ chuẩn đang hiệu lực của một (nghề, ngôn ngữ).
    ///
    /// <para><b>KHÔNG trả <c>id</c></b>: id của Interview vô nghĩa với Campaign — nó chép nội dung về
    /// và mint id riêng, nên gửi id sang chỉ mời người ta lưu một tham chiếu chết.</para>
    ///
    /// <para><b>KHÔNG trả <c>scoringScope</c></b>: Campaign không có cột tương ứng và đường chấm B2B
    /// chấm MỌI tiêu chí ở mọi câu (INT-18 chỉ áp cho B2C). Thêm một cột mà đường chấm không đọc là
    /// một cột nói dối.</para>
    /// </summary>
    [HttpGet("internal/rubrics/b2c")]
    [AllowAnonymous]
    public async Task<IActionResult> GetB2CDefaultAsync(
        [FromHeader(Name = "X-Internal-Token")] string? token,
        [FromQuery] JobCategory jobCategory,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (!TokenValid(token))
        {
            logger.LogWarning("Đọc bộ chuẩn B2C bị từ chối — X-Internal-Token sai.");
            return Unauthorized(new { error = "Invalid internal token" });
        }

        var lang = string.IsNullOrWhiteSpace(language) ? "vi" : language.Trim().ToLowerInvariant();
        if (lang is not ("vi" or "en"))
            return BadRequest(new { message = "language chỉ nhận vi hoặc en." });

        var criteria = await db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == lang && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (criteria.Count == 0) return NotFound();

        return Ok(new
        {
            jobCategory = jobCategory.ToString(),
            language = lang,
            version = criteria[0].Version,
            criteria = criteria.Select(c => new
            {
                name = c.Name,
                description = c.Description,
                weight = c.Weight,
                maxScore = c.MaxScore,
                // `.Include()` KHÔNG bảo đảm thứ tự — sắp ở đây thay vì tin vào DB, nếu không bên nhận
                // sẽ thấy mốc lộn xộn trên Postgres mà đúng thứ tự trên SQLite.
                levels = c.Levels.OrderBy(l => l.Score)
                    .Select(l => new { score = l.Score, descriptor = l.Descriptor })
            })
        });
    }

    /// <summary>
    /// So khớp HẰNG-THỜI-GIAN trên UTF-8 bytes. Fail-closed: chưa cấu hình token → từ chối hết.
    /// <c>token != expected</c> rò rỉ timing (thoát sớm ở byte lệch đầu tiên).
    /// </summary>
    private bool TokenValid(string? token)
    {
        var expected = config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }
}
