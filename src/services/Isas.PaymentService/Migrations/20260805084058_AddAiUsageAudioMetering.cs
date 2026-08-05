using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <summary>
    /// F22 — đo chi phí AI tính theo PHÚT AUDIO (model chép lời <c>whisper-1</c> bán theo phút, không theo
    /// token). Thuần additive: 2 cột nullable, KHÔNG backfill, KHÔNG raw SQL, KHÔNG đụng CHECK nào ⇒ apply
    /// được trước hay sau khi deploy code mới đều an toàn (code cũ không biết hai cột này; code mới đọc
    /// null = "lượt tính theo token", đúng ngữ nghĩa của mọi dòng đã có).
    /// </summary>
    public partial class AddAiUsageAudioMetering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "audio_seconds",
                table: "ai_usage_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_per_minute_usd",
                table: "ai_usage_logs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_seconds",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "price_per_minute_usd",
                table: "ai_usage_logs");
        }
    }
}
