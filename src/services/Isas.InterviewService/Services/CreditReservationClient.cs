using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// BC2 — typed HttpClient gọi PaymentService `/internal/credits/reserve` (máy-máy, X-Internal-Token,
// KHÔNG qua gateway). Nhái mẫu AiServiceCvAnalyzer (BC7). 402 = ví hết credit → InsufficientCreditException;
// lỗi hạ tầng → PaymentServiceException. shape req/res khớp docs/services/payment.md §/internal/credits.
public class CreditReservationClient : ICreditReservationClient
{
    private readonly HttpClient _httpClient;
    private readonly string? _internalToken;
    private readonly ILogger<CreditReservationClient> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public CreditReservationClient(
        HttpClient httpClient, IConfiguration config, ILogger<CreditReservationClient> logger)
    {
        _httpClient = httpClient;
        _internalToken = config["Internal:Token"];
        _logger = logger;
    }

    // Res 200: { reservationId, reservedCredits } (payment.md §/internal/credits/reserve).
    private record ReserveApiResponse(Guid ReservationId, int ReservedCredits);

    public async Task<CreditReservationResult> ReserveAsync(
        string ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default)
    {
        // CreditOpRequest { ownerType, ownerId, sessionId } — sessionId = idempotency key (P4).
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/credits/reserve")
        {
            Content = JsonContent.Create(new { ownerType, ownerId, sessionId })
        };
        msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được PaymentService /internal/credits/reserve");
            throw new PaymentServiceException("Không gọi được PaymentService /internal/credits/reserve", ex);
        }

        // 402 = hết credit / chạm hạn mức → KHÔNG tạo session (PAY-5).
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
            throw new InsufficientCreditException("Ví không đủ credit để bắt đầu buổi luyện");

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("PaymentService reserve lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new PaymentServiceException($"PaymentService reserve trả {(int)response.StatusCode}");
        }

        ReserveApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ReserveApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "PaymentService reserve trả JSON không hợp lệ");
            throw new PaymentServiceException("PaymentService reserve trả JSON không hợp lệ", ex);
        }

        if (body is null)
            throw new PaymentServiceException("PaymentService reserve trả rỗng");

        return new CreditReservationResult(body.ReservationId, body.ReservedCredits);
    }
}
