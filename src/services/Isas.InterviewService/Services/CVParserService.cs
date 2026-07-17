using Isas.InterviewService.DTOs;
using Isas.Shared.Files;
using System.Text.RegularExpressions;

namespace Isas.InterviewService.Services
{
    // DB17: PDF text extraction now lives in Isas.Shared (IPdfTextExtractor). Interview keeps its own
    // structured CV extraction (Name/Email/Phone/Skills/…) layered on top of the shared raw text.
    public partial class CVParserService : ICVParserService
    {
        private readonly IPdfTextExtractor _extractor;

        public CVParserService(IPdfTextExtractor extractor)
        {
            _extractor = extractor;
        }

        public async Task<CVParseResult> ParseAsync(Stream pdfStream, CancellationToken ct = default)
        {
            var extracted = await _extractor.ExtractAsync(pdfStream, ct);
            var rawText = extracted.RawText;

            return new CVParseResult
            {
                RawText = rawText,
                PageCount = extracted.PageCount,
                Email = ExtractEmail(rawText),
                Phone = ExtractPhone(rawText),
                Name = ExtractName(rawText),
                Skills = ExtractSection(rawText, "skills"),
                Experiences = ExtractSection(rawText, "experience|work experience|employment"),
                Education = ExtractSection(rawText, "education|academic")
            };
        }

        // ── Extractors ──────────────────────────────────────────────────────────

        private static string? ExtractEmail(string text)
        {
            var match = EmailRegex().Match(text);
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }

        private static string? ExtractPhone(string text)
        {
            var match = PhoneRegex().Match(text);
            return match.Success ? match.Value : null;
        }

        private static string? ExtractName(string text)
        {
            // Heuristic: first non-empty line that is mostly alphabetic and title-cased
            var firstLine = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l =>
                    l.Length is > 3 and < 60 &&
                    l.Split(' ').Length is >= 2 and <= 5 &&
                    l.All(c => char.IsLetter(c) || c == ' ' || c == '-'));

            return firstLine;
        }

        private static List<string> ExtractSection(string text, string headerPattern)
        {
            // Find the section header, then grab the next ~500 chars as bullet lines
            var headerMatch = Regex.Match(text,$@"(?i)({headerPattern})\s*[:\n]",RegexOptions.Multiline);

            if (!headerMatch.Success) return [];

            var start = headerMatch.Index + headerMatch.Length;
            var snippet = text[start..Math.Min(start + 500, text.Length)];

            return snippet
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart('-', '•', '*', '·').Trim())
                .Where(l => l.Length > 3)
                .Take(15)
                .ToList();
        }

        // ── Source-generated Regexes ────────────────────────────────────────────

        [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")]
        private static partial Regex EmailRegex();

        [GeneratedRegex(@"(\+?\d[\d\s\-().]{7,15}\d)")]
        private static partial Regex PhoneRegex();
    }
}