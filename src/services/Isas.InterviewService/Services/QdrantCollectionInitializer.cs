using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// RAG grounding — tạo collection `knowledge` lúc startup (idempotent). BEST-EFFORT: Qdrant chưa sẵn sàng
// (compose depends_on chưa healthy / server down) → LOG rồi bỏ qua, KHÔNG chặn khởi động service (retrieve
// tự degrade ungrounded). Ingest sau đó vẫn tự tạo collection nếu EnsureCollection được gọi lại — nhưng
// UpsertAsync KHÔNG tự tạo; admin ingest lúc Qdrant down sẽ 502 (đúng — không nuốt âm thầm).
public class QdrantCollectionInitializer(IVectorStore vectorStore, ILogger<QdrantCollectionInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await vectorStore.EnsureCollectionAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "RAG grounding: không khởi tạo được collection Qdrant lúc startup (retrieve sẽ degrade ungrounded " +
                "tới khi Qdrant sẵn sàng + ingest tạo lại collection).");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
