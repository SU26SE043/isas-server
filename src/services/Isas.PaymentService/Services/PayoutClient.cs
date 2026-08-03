using System.Globalization;
using Isas.PaymentService.Models;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Exceptions;
using PayOS.Models.V1.Payouts;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Impl thật của <see cref="IPayoutClient"/> bọc SDK payOS 2.1.0 (<c>Payouts</c> +
    /// <c>PayoutsAccount</c>), dùng <see cref="PayOSClient"/> của KÊNH CHI (keyed DI "payout") —
    /// credential riêng, không dùng chung với kênh thu.
    ///
    /// <para><b>Nguyên tắc phân loại lỗi:</b> chỉ những lỗi chứng minh được là "payOS đã từ chối, tiền
    /// chưa đi" mới thành <see cref="PayoutCallOutcome.Rejected"/>. Mọi thứ còn lại — timeout, mất mạng,
    /// lỗi 5xx, exception lạ — thành <see cref="PayoutCallOutcome.Unknown"/>. Lý do: sau
    /// <c>Unknown</c> hệ thống gọi LẠI bằng ĐÚNG khoá idempotency cũ (an toàn, payOS nhận ra trùng),
    /// còn sau <c>Rejected</c> nó có thể mở đường tạo lệnh mới. Đoán sai theo chiều <c>Rejected</c>
    /// là chuyển tiền hai lần; đoán sai theo chiều <c>Unknown</c> chỉ tốn thêm một lần hỏi.</para>
    ///
    /// <para><b>Không kiểm HTTP status ở đây.</b> payOS trả <c>200</c> kèm mã lỗi trong body (đo thật:
    /// <c>code "601"</c>), nên "thành công" là <c>code == "00"</c> chứ không phải 2xx. SDK đã quy đổi
    /// điều đó thành exception — nên phân loại exception là đúng chỗ, còn đọc status code thì không.</para>
    /// </summary>
    public class PayoutClient : IPayoutClient
    {
        private readonly PayOSClient? _payos;
        private readonly ILogger<PayoutClient>? _logger;

        public PayoutClient(
            [FromKeyedServices(PayoutChannelSettings.SectionName)] PayOSClient? payos,
            IOptions<PayoutChannelSettings> channel,
            ILogger<PayoutClient>? logger = null)
        {
            _payos = channel.Value.IsConfigured ? payos : null;
            _logger = logger;
        }

        public bool IsConfigured => _payos is not null;

        public async Task<PayoutCreateResult> CreateAsync(
            string referenceId,
            long amountVnd,
            string description,
            string toBin,
            string toAccountNumber,
            Guid idempotencyKey,
            CancellationToken ct = default)
        {
            if (_payos is null)
                return PayoutCreateResult.Simple(PayoutCallOutcome.Rejected,
                    "Chưa cấu hình credential kênh chi payOS (PayOS:Payout).");

            // ⚠ `ValidateDestination` CHỈ tồn tại trên lệnh chi HÀNG LOẠT, không có ở lệnh đơn (đã kiểm
            // bằng reflection trên SDK 2.1.0). Không dùng batch-của-một-phần-tử để lấy nó, vì kể cả có
            // thì nó cũng không chắn được ca đáng sợ nhất: mã ngân hàng sai mà số tài khoản đó lại TỒN
            // TẠI ở ngân hàng kia — validate "tài khoản có thật" vẫn cho qua. Lá chắn thật nằm ở chỗ
            // khác: BankBinResolver fail-closed (chỉ chi khi mã ngân hàng đã được người xác nhận), và
            // đối chiếu tên người nhận sau khi payOS báo xong.
            var request = new PayoutRequest
            {
                ReferenceId = referenceId,
                Amount = amountVnd,
                Description = description,
                ToBin = toBin,
                ToAccountNumber = toAccountNumber,
            };

            try
            {
                var payout = await _payos.Payouts.CreateAsync(request, idempotencyKey.ToString(), null);
                return new PayoutCreateResult(PayoutCallOutcome.Created, Map(payout), null);
            }
            catch (ApiException ex) when (IsDuplicateKey(ex))
            {
                // Khoá đã dùng ⇒ lệnh ĐÃ tồn tại. Không có payoutId trong lỗi, nhưng biết chắc "đã vào"
                // là đủ để KHÔNG tạo lệnh mới — phần còn lại để người đối soát.
                _logger?.LogWarning(
                    "Lệnh chi {ReferenceId}: payOS báo khoá idempotency đã tồn tại ⇒ lệnh đã được tạo " +
                    "trước đó. KHÔNG tạo lệnh mới. ({Code} {Desc})", referenceId, ex.ErrorCode, ex.ErrorDescription);
                return PayoutCreateResult.Simple(PayoutCallOutcome.AlreadyExists, ex.ErrorDescription ?? ex.Message);
            }
            catch (Exception ex) when (IsDefinitiveRejection(ex))
            {
                _logger?.LogWarning(ex, "Lệnh chi {ReferenceId} bị payOS từ chối.", referenceId);
                return PayoutCreateResult.Simple(PayoutCallOutcome.Rejected, Describe(ex));
            }
            catch (Exception ex)
            {
                // KHÔNG biết tiền đã đi hay chưa → tuyệt đối không tạo lệnh mới bằng khoá khác.
                _logger?.LogError(ex,
                    "Lệnh chi {ReferenceId} KHÔNG rõ kết quả (timeout/lỗi mạng). Sẽ hỏi lại bằng ĐÚNG " +
                    "khoá idempotency cũ, không tạo lệnh mới.", referenceId);
                return PayoutCreateResult.Simple(PayoutCallOutcome.Unknown, Describe(ex));
            }
        }

        public async Task<PayoutSnapshot?> GetAsync(string payoutId, CancellationToken ct = default)
        {
            if (_payos is null) return null;

            try
            {
                var payout = await _payos.Payouts.GetAsync(payoutId, null);
                return Map(payout);
            }
            catch (Exception ex)
            {
                // null = "không tra được". Caller KHÔNG được suy thành thành công hay thất bại.
                _logger?.LogWarning(ex, "Không tra được trạng thái lệnh chi {PayoutId}.", payoutId);
                return null;
            }
        }

        public async Task<long?> GetBalanceAsync(CancellationToken ct = default)
        {
            if (_payos is null) return null;

            try
            {
                var info = await _payos.PayoutsAccount.GetBalanceAsync(null);
                // payOS trả số dư dưới dạng CHUỖI. Không parse được → null ("không biết"), tuyệt đối
                // không quy về 0: 0 sẽ bị đọc thành "hết tiền" và chặn oan mọi lệnh hoàn.
                return long.TryParse(info?.Balance, NumberStyles.Integer, CultureInfo.InvariantCulture, out var balance)
                    ? balance
                    : null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Không đọc được số dư ví chi payOS.");
                return null;
            }
        }

        /// <summary>
        /// Gộp state payOS về 3 nhánh hành động. <c>Received</c>/<c>Processing</c> đều là CHƯA XONG.
        /// Trạng thái lạ (payOS thêm state mới) rơi vào <see cref="PayoutState.InFlight"/> — an toàn hơn
        /// <c>Succeeded</c> (đóng dấu oan) và hơn <c>Failed</c> (báo hỏng oan cho lệnh đang chạy).
        /// </summary>
        private static PayoutSnapshot Map(Payout payout)
        {
            var txn = payout.Transactions?.FirstOrDefault();

            var state = txn?.State switch
            {
                PayoutTransactionState.Succeeded => PayoutState.Succeeded,
                PayoutTransactionState.Failed => PayoutState.Failed,
                PayoutTransactionState.Cancelled => PayoutState.Failed,
                _ => PayoutState.InFlight
            };

            var message = state == PayoutState.Failed
                ? $"{txn?.ErrorCode} {txn?.ErrorMessage}".Trim()
                : null;

            return new PayoutSnapshot(state, payout.Id, txn?.ToAccountName, message);
        }

        // Chỉ những lỗi CHỨNG MINH ĐƯỢC là "chưa vào hệ thống payOS". Cố ý KHÔNG gồm 5xx và
        // TooManyRequests: 5xx có thể xảy ra sau khi lệnh đã được ghi nhận, còn với 429 thì việc hỏi lại
        // bằng khoá cũ vốn đã an toàn nên không cần mạo hiểm phân loại.
        public static bool IsDefinitiveRejection(Exception ex) =>
            ex is BadRequestException or UnauthorizedException or ForbiddenException or NotFoundException;

        // Nhận diện "khoá idempotency đã tồn tại" theo mô tả lỗi. Best-effort: KHÔNG khớp thì rơi xuống
        // nhánh Unknown (an toàn) chứ không rơi xuống Rejected.
        public static bool IsDuplicateKey(ApiException ex) =>
            (ex.ErrorDescription ?? ex.Message ?? "").Contains("idempotency", StringComparison.OrdinalIgnoreCase);

        private static string Describe(Exception ex) =>
            ex is ApiException api
                ? $"[{api.ErrorCode}] {api.ErrorDescription ?? api.Message}"
                : ex.Message;
    }
}
