using Isas.InterviewService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services
{
    public class AudioService : IAudioService
    {
        private readonly IStorageService _storage;
        private readonly InterviewDbContext _db;
        private readonly ILogger<AudioService> _logger;

        public AudioService(IStorageService storage, InterviewDbContext db, ILogger<AudioService> logger)
        {
            _storage = storage;
            _db = db;
            _logger = logger;
        }

        public Task<bool> DeleteAudioAsync(Guid audioId, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task<AnswerAudio> GetAudioByIdAsync(Guid id, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task<List<AnswerAudio>> GetAudiosByAnswerAsync(Guid answerId, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task<AnswerAudio> UpdateAudioAsync(Guid audioId, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public async Task<AnswerAudio> UploadAudioAsync(IFormFile file, Guid answerId, string userId, CancellationToken ct)
        {
            await using var stream = file.OpenReadStream();

            string storagePath = await _storage.UploadAsync(
                fileStream: stream,
                fileType: "audio",
                userId: userId,
                fileId: Guid.NewGuid(),
                ext: "opus",
                ct: ct);

            var answerAudio = new AnswerAudio
            {
                Id = Guid.NewGuid(),
                AnswerId = answerId,
                UserId = Guid.Parse(userId),
                StorageKey = storagePath,
                UploadedAt = DateTime.UtcNow,
                ExpiredAt = DateTime.UtcNow.AddDays(7),
                IsRetained = false
            };

            _db.AnswerAudios.Add(answerAudio);
            await _db.SaveChangesAsync(ct);

            return answerAudio;
        }
    }
}