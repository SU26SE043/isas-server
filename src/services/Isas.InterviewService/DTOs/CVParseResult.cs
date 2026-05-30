namespace Isas.InterviewService.DTOs
{
    public class CVParseResult
    {
        public string RawText { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public List<string> Skills { get; set; } = [];
        public List<string> Experiences { get; set; } = [];
        public List<string> Education { get; set; } = [];
        public int PageCount { get; set; }
    }
}
