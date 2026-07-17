namespace Isas.Shared.Files;

/// <summary>Result of extracting raw text from a PDF: the concatenated page text and the page count.</summary>
public sealed record PdfExtractResult(string RawText, int PageCount);
