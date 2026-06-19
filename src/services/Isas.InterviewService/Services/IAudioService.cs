using Isas.InterviewService.Models;

namespace Isas.InterviewService.Services
{
    public interface IAudioService
    {
        Task<AnswerAudio> UploadAudioAsync(IFormFile file, Guid answerId, string userId, CancellationToken ct);
        Task<List<AnswerAudio>> GetAudiosByAnswerAsync(Guid answerId, CancellationToken ct);
        Task<AnswerAudio> GetAudioByIdAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteAudioAsync(Guid audioId, CancellationToken ct);
        Task<AnswerAudio> UpdateAudioAsync(Guid audioId, CancellationToken ct);
    }
}
