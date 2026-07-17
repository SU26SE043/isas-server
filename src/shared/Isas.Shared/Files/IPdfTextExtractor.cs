namespace Isas.Shared.Files;

/// <summary>
/// DB17 — the single owner of PDF → plain-text extraction, shared by CampaignService and
/// InterviewService (previously each carried a byte-identical PdfPig implementation). Services keep
/// their own higher-level parsers (raw-only vs. + structured CV fields) on top of this primitive.
/// </summary>
public interface IPdfTextExtractor
{
    /// <summary>Extract concatenated page text + page count from a PDF stream. Runs the (synchronous,
    /// CPU-bound) PdfPig read off the calling thread. The stream is rewound to position 0 if seekable.</summary>
    Task<PdfExtractResult> ExtractAsync(Stream pdfStream, CancellationToken ct = default);
}
