namespace Isas.AuthService.DTOs;

public sealed class AuthAnalyticsResponse
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string Granularity { get; init; } = default!;
    public AuthAnalyticsTotals Totals { get; init; } = default!;
    public AuthActiveUsers ActiveUsers { get; init; } = default!;
    public IReadOnlyList<AuthAnalyticsBucket> Buckets { get; init; } = Array.Empty<AuthAnalyticsBucket>();
}

public sealed class AuthAnalyticsTotals
{
    public int TotalUsers { get; init; }
    public int NewUsers { get; init; }
    public int BannedUsers { get; init; }
    public int TotalOrganizations { get; init; }
    public IReadOnlyList<RoleCount> ByRole { get; init; } = Array.Empty<RoleCount>();
}

public sealed record RoleCount(string Role, int Count);
public sealed record AuthActiveUsers(int Last7Days, int Last30Days);
public sealed record AuthAnalyticsBucket(DateTime PeriodStart, int NewUsers, int Logins, int DistinctUsers);
