namespace Isas.CampaignService.Services
{
    public interface IFileService
    {
        Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default);
        Task DeleteAsync(string path, CancellationToken ct = default);
        Task<Stream> DownloadAsync(string path, CancellationToken ct = default);
        string GetUrl(string path);
    }
}
