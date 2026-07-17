using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Isas.Shared.Pagination;

/// <summary>
/// Opaque keyset (seek) cursor over a <c>(CreatedAt DESC, Id DESC)</c> ordering. Encodes the last
/// row's sort key so the next page resumes without OFFSET (constant-cost paging at scale — DB8).
/// Wire form: URL-safe base64 of <c>"{createdAt.Ticks}:{id:N}"</c>. Decoding is total — any malformed
/// value yields <c>null</c> (treated as "first page"), never an exception, so a bad cursor is never a 500.
/// </summary>
public sealed record KeysetCursor(DateTime CreatedAt, Guid Id)
{
    public string Encode()
    {
        // Ticks preserves the exact stored instant; reconstructed as UTC on decode to match how
        // Npgsql reads timestamptz. Id as 32 hex digits ("N") — compact, unambiguous separator-free.
        var raw = $"{CreatedAt.Ticks}:{Id:N}";
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Decode a cursor; returns <c>null</c> for null/empty/malformed input (first page).</summary>
    public static KeysetCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var raw = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            var sep = raw.IndexOf(':');
            if (sep <= 0)
                return null;

            if (!long.TryParse(raw.AsSpan(0, sep), out var ticks))
                return null;
            if (!Guid.TryParseExact(raw.AsSpan(sep + 1), "N", out var id))
                return null;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return null;

            return new KeysetCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (FormatException)
        {
            // Base64UrlDecode throws FormatException on invalid input — swallow to "first page".
            return null;
        }
    }
}
