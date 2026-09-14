using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Dựng <see cref="PracticeService"/> thật với mọi dependency KHÔNG liên quan mock no-op — dùng cho
/// test chỉ chạm đường đọc/ghi DbContext (mẫu <c>InternalSessionsExistsTests.BuildService</c>).
/// </summary>
internal static class PracticeServiceFactory
{
    public static PracticeService ForFocusTests(InterviewDbContext db)
        => new(
            db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);
}
