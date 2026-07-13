namespace Isas.PaymentService.Services
{
    /// <summary>
    /// E7: xử lý event session từ InterviewService (interview.events) → tiêu/nhả credit.
    /// Tách khỏi việc consume RabbitMQ (<see cref="InterviewEventConsumer"/>) để UNIT-TEST được
    /// bằng message giả, không cần broker thật (như E4 <c>RankingEventHandler</c>).
    ///
    /// Handler tự OWN routing theo routing-key + deserialize payload → gọi <see cref="ICreditAccountService"/>
    /// (P5/P6). Nhờ vậy nhánh "key lạ → bỏ qua" cũng test được ở tầng handler.
    /// </summary>
    public interface ICreditEventHandler
    {
        /// <param name="routingKey">
        /// Routing key của message (<c>session.scored</c> → consume; <c>session.abandoned</c> → release;
        /// khác → bỏ qua).
        /// </param>
        /// <param name="json">Body message (UTF-8 JSON) từ InterviewService.</param>
        Task HandleAsync(string routingKey, string json, CancellationToken ct = default);
    }
}
