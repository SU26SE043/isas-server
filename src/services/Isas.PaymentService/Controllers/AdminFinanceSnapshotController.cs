using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// AUTH-7: chỉ số tài chính kiểu SỐ DƯ (AR + MRR) cho PlatformAdmin — xem
    /// <see cref="FinanceSnapshotResponse"/> cho lý do tách endpoint này khỏi <c>/admin/revenue</c>
    /// (F19, dòng chảy theo kỳ) thay vì gộp chung. Route "admin" → gateway strip /api/v1/payment →
    /// /api/v1/payment/admin/finance-snapshot.
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminFinanceSnapshotController : ControllerBase
    {
        private readonly IFinanceSnapshotService _snapshot;

        public AdminFinanceSnapshotController(IFinanceSnapshotService snapshot) => _snapshot = snapshot;

        /// <summary>
        /// GET /payment/admin/finance-snapshot — không nhận <c>from</c>/<c>to</c>: AR và MRR là số dư
        /// TẠI THỜI ĐIỂM GỌI (<see cref="FinanceSnapshotResponse.AsOf"/>), không phải dòng chảy trong kỳ.
        /// </summary>
        [HttpGet("finance-snapshot")]
        public async Task<ActionResult<FinanceSnapshotResponse>> GetFinanceSnapshot(CancellationToken ct)
            => Ok(await _snapshot.GetSnapshotAsync(ct));
    }
}
