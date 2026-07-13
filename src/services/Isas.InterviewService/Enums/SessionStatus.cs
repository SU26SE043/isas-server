namespace Isas.InterviewService.Enums;

public enum SessionStatus
{
    GeneratingQuestions,
    Ready,
    InProgress,
    Completed,
    Scoring,
    Scored,
    Failed,

    // E3: InProgress quá hạn mà KHÔNG có answer nào -> bỏ ngang (SessionAbandonSweeper đóng
    // session + phát event SessionAbandoned cho Payment release reservation). Nhánh ≥1 answer
    // (auto-submit khi quá hạn) là task I2, chưa build — không xử lý ở đây.
    SessionAbandoned
}