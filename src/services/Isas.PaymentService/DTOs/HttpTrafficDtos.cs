namespace Isas.PaymentService.DTOs;
public sealed record RecordHttpTrafficRequest(DateTime WindowStart, DateTime WindowEnd, string RouteId, string StatusClass, int Requests, long SumDurationMs, int MaxDurationMs);
