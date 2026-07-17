namespace Isas.Shared.Pagination;

/// <summary>
/// A single keyset page: the page rows plus an opaque cursor for the next page
/// (<c>null</c> when the last page was returned). Admin controllers write <see cref="Items"/>
/// to the (unchanged, array) response body and <see cref="NextCursor"/> to the
/// <c>X-Next-Cursor</c> response header — keeping the existing API contract backward-compatible.
/// </summary>
public sealed record KeysetPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public static KeysetPage<T> Empty { get; } = new([], null);
}

/// <summary>Shared limits/helpers for keyset admin paging (DB8).</summary>
public static class KeysetPaging
{
    /// <summary>Default page size — equals the previous hard cap, so a request without
    /// <c>?limit</c> behaves identically to the pre-DB8 endpoint (backward compatible).</summary>
    public const int DefaultLimit = 500;

    /// <summary>Upper bound on a single page to keep query cost bounded; page past it via the cursor.</summary>
    public const int MaxLimit = 500;

    /// <summary>Name of the response header carrying the next-page cursor.</summary>
    public const string NextCursorHeader = "X-Next-Cursor";

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
