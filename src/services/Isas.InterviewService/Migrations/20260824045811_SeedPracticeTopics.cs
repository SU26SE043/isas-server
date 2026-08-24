using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class SeedPracticeTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "practice_topics",
                columns: new[] { "id", "criterion_name", "display_order", "is_active", "job_category", "label", "language", "seniority", "topic_key", "version" },
                values: new object[,]
                {
                    { new Guid("1ba00000-0000-0000-0000-000000000001"), "Phân tích yêu cầu", 1, true, "BA", "User story: cấu trúc và cách viết cơ bản", "vi", "Fresher", "top1.ba.fresher.user-story-basics", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000002"), "Phân tích yêu cầu", 2, true, "BA", "Use case: xác định tác nhân (actor) và luồng chính", "vi", "Fresher", "top1.ba.fresher.use-case-basics", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000003"), "Phân tích yêu cầu", 3, true, "BA", "Tài liệu đặc tả yêu cầu (SRS): mục đích và nội dung chính", "vi", "Fresher", "top1.ba.fresher.srs-purpose", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000004"), "Phân tích yêu cầu", 4, true, "BA", "Phân biệt yêu cầu chức năng và phi chức năng qua ví dụ", "vi", "Fresher", "top1.ba.fresher.functional-vs-nonfunctional", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000005"), "Phân tích yêu cầu", 5, true, "BA", "Đọc và diễn giải lại một yêu cầu cụ thể bằng lời của mình", "vi", "Fresher", "top1.ba.fresher.read-single-requirement", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000006"), "Tư duy giải quyết vấn đề", 6, true, "BA", "Đặt câu hỏi làm rõ khi một yêu cầu chưa rõ ràng", "vi", "Fresher", "top1.ba.fresher.clarifying-question", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000007"), "Hiểu nghiệp vụ & các bên liên quan", 7, true, "BA", "Trao đổi với một stakeholder để xác nhận hiểu đúng yêu cầu", "vi", "Fresher", "top1.ba.fresher.single-stakeholder-check", 1 },
                    { new Guid("1ba00000-0000-0000-0000-000000000008"), "Hiểu nghiệp vụ & các bên liên quan", 8, true, "BA", "Vai trò và trách nhiệm cơ bản của BA trong dự án", "vi", "Fresher", "top1.ba.fresher.ba-role-basics", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000001"), "Requirements analysis", 1, true, "BA", "User stories: structure and how to write one", "en", "Fresher", "top1.ba.fresher.user-story-basics", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000002"), "Requirements analysis", 2, true, "BA", "Use cases: identifying the actor and the main flow", "en", "Fresher", "top1.ba.fresher.use-case-basics", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000003"), "Requirements analysis", 3, true, "BA", "Software Requirements Specification (SRS): purpose and typical content", "en", "Fresher", "top1.ba.fresher.srs-purpose", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000004"), "Requirements analysis", 4, true, "BA", "Telling functional and non-functional requirements apart, with examples", "en", "Fresher", "top1.ba.fresher.functional-vs-nonfunctional", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000005"), "Requirements analysis", 5, true, "BA", "Reading a single requirement and restating it in your own words", "en", "Fresher", "top1.ba.fresher.read-single-requirement", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000006"), "Problem solving", 6, true, "BA", "Asking a clarifying question when a requirement is unclear", "en", "Fresher", "top1.ba.fresher.clarifying-question", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000007"), "Business domain & stakeholders", 7, true, "BA", "Talking with one stakeholder to confirm you understood a requirement correctly", "en", "Fresher", "top1.ba.fresher.single-stakeholder-check", 1 },
                    { new Guid("1ba00011-0000-0000-0000-000000000008"), "Business domain & stakeholders", 8, true, "BA", "A BA's basic role and responsibilities on a project", "en", "Fresher", "top1.ba.fresher.ba-role-basics", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000001"), "Phân tích yêu cầu", 1, true, "BA", "Tự viết user story/use case hoàn chỉnh cho một tính năng", "vi", "Junior", "top1.ba.junior.write-user-story-full-feature", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000002"), "Hiểu nghiệp vụ & các bên liên quan", 2, true, "BA", "Chạy workshop thu thập yêu cầu với 1-2 stakeholder", "vi", "Junior", "top1.ba.junior.workshop-few-stakeholders", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000003"), "Phân tích yêu cầu", 3, true, "BA", "Viết acceptance criteria rõ ràng cho một tính năng", "vi", "Junior", "top1.ba.junior.acceptance-criteria", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000004"), "Phân tích yêu cầu", 4, true, "BA", "Phát hiện yêu cầu mơ hồ hoặc thiếu sót", "vi", "Junior", "top1.ba.junior.spot-ambiguous-requirement", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000005"), "Tư duy giải quyết vấn đề", 5, true, "BA", "Hỏi lại đúng chỗ khi phát hiện vấn đề trong yêu cầu", "vi", "Junior", "top1.ba.junior.ask-follow-up-right-spot", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000006"), "Tư duy giải quyết vấn đề", 6, true, "BA", "Xử lý tình huống khách hàng đổi ý giữa chừng", "vi", "Junior", "top1.ba.junior.client-changes-mind", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000007"), "Tư duy giải quyết vấn đề", 7, true, "BA", "Xử lý yêu cầu chồng chéo giữa hai bộ phận", "vi", "Junior", "top1.ba.junior.conflicting-department-requirements", 1 },
                    { new Guid("1ba01000-0000-0000-0000-000000000008"), "Hiểu nghiệp vụ & các bên liên quan", 8, true, "BA", "Làm việc trực tiếp với 1-2 stakeholder trong một buổi workshop", "vi", "Junior", "top1.ba.junior.small-workshop-facilitation", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000001"), "Requirements analysis", 1, true, "BA", "Writing a complete user story or use case for one feature on your own", "en", "Junior", "top1.ba.junior.write-user-story-full-feature", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000002"), "Business domain & stakeholders", 2, true, "BA", "Running a requirements-gathering workshop with one or two stakeholders", "en", "Junior", "top1.ba.junior.workshop-few-stakeholders", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000003"), "Requirements analysis", 3, true, "BA", "Writing clear acceptance criteria for a feature", "en", "Junior", "top1.ba.junior.acceptance-criteria", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000004"), "Requirements analysis", 4, true, "BA", "Spotting a requirement that is ambiguous or incomplete", "en", "Junior", "top1.ba.junior.spot-ambiguous-requirement", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000005"), "Problem solving", 5, true, "BA", "Asking the right follow-up question when something in a requirement looks off", "en", "Junior", "top1.ba.junior.ask-follow-up-right-spot", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000006"), "Problem solving", 6, true, "BA", "Handling a situation where the client changes their mind midway through", "en", "Junior", "top1.ba.junior.client-changes-mind", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BA", "Handling requirements that conflict between two departments", "en", "Junior", "top1.ba.junior.conflicting-department-requirements", 1 },
                    { new Guid("1ba01011-0000-0000-0000-000000000008"), "Business domain & stakeholders", 8, true, "BA", "Working directly with one or two stakeholders during a small workshop", "en", "Junior", "top1.ba.junior.small-workshop-facilitation", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000001"), "Hiểu nghiệp vụ & các bên liên quan", 1, true, "BA", "Chủ trì workshop với nhiều stakeholder có quan điểm mâu thuẫn", "vi", "Middle", "top1.ba.middle.workshop-conflicting-stakeholders", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000002"), "Phân tích yêu cầu", 2, true, "BA", "Vẽ quy trình nghiệp vụ (process mapping) cho một luồng công việc", "vi", "Middle", "top1.ba.middle.process-mapping", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000003"), "Tư duy giải quyết vấn đề", 3, true, "BA", "Phân tích đánh đổi giữa phạm vi và thời hạn dự án", "vi", "Middle", "top1.ba.middle.scope-vs-deadline", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000004"), "Tư duy giải quyết vấn đề", 4, true, "BA", "So sánh phương án tự xây dựng và mua giải pháp có sẵn", "vi", "Middle", "top1.ba.middle.build-vs-buy", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000005"), "Phân tích yêu cầu", 5, true, "BA", "Đánh giá tác động khi yêu cầu thay đổi giữa dự án", "vi", "Middle", "top1.ba.middle.change-impact-analysis", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000006"), "Hiểu nghiệp vụ & các bên liên quan", 6, true, "BA", "Xử lý xung đột lợi ích giữa các bên liên quan", "vi", "Middle", "top1.ba.middle.stakeholder-conflict-of-interest", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000007"), "Tư duy giải quyết vấn đề", 7, true, "BA", "Ưu tiên hoá backlog theo giá trị nghiệp vụ", "vi", "Middle", "top1.ba.middle.backlog-prioritization", 1 },
                    { new Guid("1ba02000-0000-0000-0000-000000000008"), "Hiểu nghiệp vụ & các bên liên quan", 8, true, "BA", "Dẫn dắt một cuộc họp yêu cầu phức tạp", "vi", "Middle", "top1.ba.middle.facilitate-complex-meeting", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000001"), "Business domain & stakeholders", 1, true, "BA", "Facilitating a workshop with multiple stakeholders who disagree", "en", "Middle", "top1.ba.middle.workshop-conflicting-stakeholders", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000002"), "Requirements analysis", 2, true, "BA", "Mapping a business process for one workflow", "en", "Middle", "top1.ba.middle.process-mapping", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000003"), "Problem solving", 3, true, "BA", "Weighing the trade-off between project scope and deadline", "en", "Middle", "top1.ba.middle.scope-vs-deadline", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000004"), "Problem solving", 4, true, "BA", "Comparing building a solution in-house versus buying one", "en", "Middle", "top1.ba.middle.build-vs-buy", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000005"), "Requirements analysis", 5, true, "BA", "Assessing the impact when a requirement changes mid-project", "en", "Middle", "top1.ba.middle.change-impact-analysis", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000006"), "Business domain & stakeholders", 6, true, "BA", "Handling a conflict of interest between stakeholders", "en", "Middle", "top1.ba.middle.stakeholder-conflict-of-interest", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BA", "Prioritizing the backlog by business value", "en", "Middle", "top1.ba.middle.backlog-prioritization", 1 },
                    { new Guid("1ba02011-0000-0000-0000-000000000008"), "Business domain & stakeholders", 8, true, "BA", "Leading a complex requirements meeting", "en", "Middle", "top1.ba.middle.facilitate-complex-meeting", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000001"), "Phân tích yêu cầu", 1, true, "BA", "Định hình giải pháp cho cả một mảng nghiệp vụ", "vi", "Senior", "top1.ba.senior.shape-business-area-solution", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000002"), "Tư duy giải quyết vấn đề", 2, true, "BA", "Cân bằng ràng buộc kỹ thuật, ngân sách và chính trị nội bộ", "vi", "Senior", "top1.ba.senior.balance-tech-budget-politics", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000003"), "Hiểu nghiệp vụ & các bên liên quan", 3, true, "BA", "Dẫn dắt và kèm cặp BA/PO junior", "vi", "Senior", "top1.ba.senior.mentor-junior-ba", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000004"), "Phân tích yêu cầu", 4, true, "BA", "Chịu trách nhiệm chất lượng yêu cầu ở quy mô nhiều dự án", "vi", "Senior", "top1.ba.senior.requirement-quality-multi-project", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000005"), "Tư duy giải quyết vấn đề", 5, true, "BA", "Ra quyết định khi thiếu thông tin đầy đủ", "vi", "Senior", "top1.ba.senior.decide-with-incomplete-info", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000006"), "Hiểu nghiệp vụ & các bên liên quan", 6, true, "BA", "Thuyết phục stakeholder cấp cao", "vi", "Senior", "top1.ba.senior.persuade-senior-stakeholder", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000007"), "Tư duy giải quyết vấn đề", 7, true, "BA", "Đo lường giá trị nghiệp vụ sau khi triển khai", "vi", "Senior", "top1.ba.senior.measure-value-after-rollout", 1 },
                    { new Guid("1ba03000-0000-0000-0000-000000000008"), "Phân tích yêu cầu", 8, true, "BA", "Quản lý rủi ro chất lượng yêu cầu ở quy mô lớn", "vi", "Senior", "top1.ba.senior.manage-requirement-risk-at-scale", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000001"), "Requirements analysis", 1, true, "BA", "Shaping the solution direction for an entire business area", "en", "Senior", "top1.ba.senior.shape-business-area-solution", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000002"), "Problem solving", 2, true, "BA", "Balancing technical, budget, and internal-politics constraints", "en", "Senior", "top1.ba.senior.balance-tech-budget-politics", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000003"), "Business domain & stakeholders", 3, true, "BA", "Mentoring and guiding junior BAs or POs", "en", "Senior", "top1.ba.senior.mentor-junior-ba", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000004"), "Requirements analysis", 4, true, "BA", "Owning requirement quality across multiple projects", "en", "Senior", "top1.ba.senior.requirement-quality-multi-project", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000005"), "Problem solving", 5, true, "BA", "Making a decision when information is incomplete", "en", "Senior", "top1.ba.senior.decide-with-incomplete-info", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000006"), "Business domain & stakeholders", 6, true, "BA", "Persuading a senior stakeholder", "en", "Senior", "top1.ba.senior.persuade-senior-stakeholder", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BA", "Measuring business value after a solution goes live", "en", "Senior", "top1.ba.senior.measure-value-after-rollout", 1 },
                    { new Guid("1ba03011-0000-0000-0000-000000000008"), "Requirements analysis", 8, true, "BA", "Managing requirement-quality risk at scale", "en", "Senior", "top1.ba.senior.manage-requirement-risk-at-scale", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000001"), "Chiều sâu kỹ thuật", 1, true, "BE", "Cấu trúc dữ liệu mảng và list: khi nào dùng cái nào", "vi", "Fresher", "top1.be.fresher.array-list-basics", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "BE", "Hash map: khái niệm và tình huống áp dụng", "vi", "Fresher", "top1.be.fresher.hash-map-basics", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "BE", "Viết một API CRUD đơn giản cho một tài nguyên", "vi", "Fresher", "top1.be.fresher.simple-crud-api", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000004"), "Thiết kế hệ thống & CSDL", 4, true, "BE", "Câu lệnh SQL SELECT cơ bản để lấy dữ liệu", "vi", "Fresher", "top1.be.fresher.sql-select-basics", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000005"), "Thiết kế hệ thống & CSDL", 5, true, "BE", "Câu lệnh SQL INSERT và UPDATE cơ bản", "vi", "Fresher", "top1.be.fresher.sql-insert-update-basics", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000006"), "Thiết kế hệ thống & CSDL", 6, true, "BE", "Viết một câu JOIN đơn giản giữa hai bảng", "vi", "Fresher", "top1.be.fresher.simple-join", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000007"), "Giải quyết vấn đề & thuật toán", 7, true, "BE", "Phân biệt và chọn đúng HTTP method cho một thao tác", "vi", "Fresher", "top1.be.fresher.http-method-choice", 1 },
                    { new Guid("1be00000-0000-0000-0000-000000000008"), "Giải quyết vấn đề & thuật toán", 8, true, "BE", "Vì sao một thao tác API nên dùng đúng HTTP method", "vi", "Fresher", "top1.be.fresher.http-method-why", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000001"), "Technical depth", 1, true, "BE", "Arrays vs. lists: when to use which", "en", "Fresher", "top1.be.fresher.array-list-basics", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "BE", "Hash maps: the concept and when to use one", "en", "Fresher", "top1.be.fresher.hash-map-basics", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "BE", "Writing a simple CRUD API for one resource", "en", "Fresher", "top1.be.fresher.simple-crud-api", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000004"), "System design & databases", 4, true, "BE", "Basic SQL SELECT to fetch data", "en", "Fresher", "top1.be.fresher.sql-select-basics", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000005"), "System design & databases", 5, true, "BE", "Basic SQL INSERT and UPDATE statements", "en", "Fresher", "top1.be.fresher.sql-insert-update-basics", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000006"), "System design & databases", 6, true, "BE", "Writing a simple JOIN across two tables", "en", "Fresher", "top1.be.fresher.simple-join", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BE", "Telling HTTP methods apart and picking the right one for an action", "en", "Fresher", "top1.be.fresher.http-method-choice", 1 },
                    { new Guid("1be00011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "BE", "Why an API action should use the correct HTTP method", "en", "Fresher", "top1.be.fresher.http-method-why", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000001"), "Chiều sâu kỹ thuật", 1, true, "BE", "Validate input đầu vào cho một API", "vi", "Junior", "top1.be.junior.validate-input", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "BE", "Xử lý lỗi và trả đúng status code", "vi", "Junior", "top1.be.junior.error-handling-status-code", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "BE", "Viết một API hoàn chỉnh cho một tính năng cụ thể", "vi", "Junior", "top1.be.junior.full-feature-api", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000004"), "Thiết kế hệ thống & CSDL", 4, true, "BE", "Viết truy vấn có JOIN và GROUP BY", "vi", "Junior", "top1.be.junior.join-group-by", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000005"), "Thiết kế hệ thống & CSDL", 5, true, "BE", "Cơ chế index cơ bản: khi nào một truy vấn cần index", "vi", "Junior", "top1.be.junior.index-basics", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000006"), "Thiết kế hệ thống & CSDL", 6, true, "BE", "Chẩn đoán vì sao một truy vấn chạy chậm do thiếu index", "vi", "Junior", "top1.be.junior.slow-query-missing-index", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000007"), "Giải quyết vấn đề & thuật toán", 7, true, "BE", "Debug một lỗi runtime thường gặp", "vi", "Junior", "top1.be.junior.debug-runtime-error", 1 },
                    { new Guid("1be01000-0000-0000-0000-000000000008"), "Giải quyết vấn đề & thuật toán", 8, true, "BE", "Chẩn đoán vì sao API trả sai dữ liệu do thiếu điều kiện lọc", "vi", "Junior", "top1.be.junior.wrong-data-missing-filter", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000001"), "Technical depth", 1, true, "BE", "Validating input for an API", "en", "Junior", "top1.be.junior.validate-input", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "BE", "Handling errors and returning the right status code", "en", "Junior", "top1.be.junior.error-handling-status-code", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "BE", "Writing a complete API for one feature", "en", "Junior", "top1.be.junior.full-feature-api", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000004"), "System design & databases", 4, true, "BE", "Writing a query with JOIN and GROUP BY", "en", "Junior", "top1.be.junior.join-group-by", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000005"), "System design & databases", 5, true, "BE", "Basic indexing: when a query needs an index", "en", "Junior", "top1.be.junior.index-basics", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000006"), "System design & databases", 6, true, "BE", "Diagnosing why a query is slow because of a missing index", "en", "Junior", "top1.be.junior.slow-query-missing-index", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BE", "Debugging a common runtime error", "en", "Junior", "top1.be.junior.debug-runtime-error", 1 },
                    { new Guid("1be01011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "BE", "Diagnosing why an API returns wrong data because of a missing filter condition", "en", "Junior", "top1.be.junior.wrong-data-missing-filter", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000001"), "Thiết kế hệ thống & CSDL", 1, true, "BE", "Thiết kế schema database cho một module cụ thể", "vi", "Middle", "top1.be.middle.module-schema-design", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000002"), "Thiết kế hệ thống & CSDL", 2, true, "BE", "Chọn giữa các phương án lưu trữ dữ liệu và caching", "vi", "Middle", "top1.be.middle.storage-caching-choice", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000003"), "Thiết kế hệ thống & CSDL", 3, true, "BE", "Tối ưu một truy vấn đang chạy chậm", "vi", "Middle", "top1.be.middle.optimize-slow-query", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000004"), "Chiều sâu kỹ thuật", 4, true, "BE", "Xử lý race condition trong hệ thống đồng thời", "vi", "Middle", "top1.be.middle.race-condition", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000005"), "Chiều sâu kỹ thuật", 5, true, "BE", "Xử lý deadlock giữa các giao dịch", "vi", "Middle", "top1.be.middle.deadlock", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000006"), "Chiều sâu kỹ thuật", 6, true, "BE", "Viết test cho logic nghiệp vụ phức tạp", "vi", "Middle", "top1.be.middle.test-complex-logic", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000007"), "Giải quyết vấn đề & thuật toán", 7, true, "BE", "Đánh đổi giữa consistency và performance khi thiết kế", "vi", "Middle", "top1.be.middle.consistency-vs-performance", 1 },
                    { new Guid("1be02000-0000-0000-0000-000000000008"), "Giải quyết vấn đề & thuật toán", 8, true, "BE", "Gỡ lỗi một hệ thống đang chạy thật trong production", "vi", "Middle", "top1.be.middle.debug-production-system", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000001"), "System design & databases", 1, true, "BE", "Designing a database schema for one module", "en", "Middle", "top1.be.middle.module-schema-design", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000002"), "System design & databases", 2, true, "BE", "Choosing between storage and caching approaches", "en", "Middle", "top1.be.middle.storage-caching-choice", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000003"), "System design & databases", 3, true, "BE", "Optimizing a slow-running query", "en", "Middle", "top1.be.middle.optimize-slow-query", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000004"), "Technical depth", 4, true, "BE", "Handling a race condition in a concurrent system", "en", "Middle", "top1.be.middle.race-condition", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000005"), "Technical depth", 5, true, "BE", "Handling a deadlock between transactions", "en", "Middle", "top1.be.middle.deadlock", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000006"), "Technical depth", 6, true, "BE", "Writing tests for complex business logic", "en", "Middle", "top1.be.middle.test-complex-logic", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "BE", "Trading off consistency against performance in a design", "en", "Middle", "top1.be.middle.consistency-vs-performance", 1 },
                    { new Guid("1be02011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "BE", "Debugging a live production system", "en", "Middle", "top1.be.middle.debug-production-system", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000001"), "Thiết kế hệ thống & CSDL", 1, true, "BE", "Thiết kế kiến trúc hệ thống nhiều service", "vi", "Senior", "top1.be.senior.multi-service-architecture", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "BE", "Cơ chế đồng bộ dữ liệu giữa các service", "vi", "Senior", "top1.be.senior.data-sync-across-services", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "BE", "Idempotency khi thiết kế API/service", "vi", "Senior", "top1.be.senior.idempotency", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000004"), "Chiều sâu kỹ thuật", 4, true, "BE", "Chiến lược retry và backoff khi gọi service khác", "vi", "Senior", "top1.be.senior.retry-backoff", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000005"), "Thiết kế hệ thống & CSDL", 5, true, "BE", "Đánh giá đánh đổi giữa các mô hình lưu trữ ở quy mô lớn", "vi", "Senior", "top1.be.senior.storage-tradeoff-at-scale", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000006"), "Giải quyết vấn đề & thuật toán", 6, true, "BE", "Xử lý sự cố sản xuất (production incident)", "vi", "Senior", "top1.be.senior.production-incident", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000007"), "Thiết kế hệ thống & CSDL", 7, true, "BE", "Thiết kế hệ thống chịu lỗi (fault-tolerant)", "vi", "Senior", "top1.be.senior.fault-tolerant-design", 1 },
                    { new Guid("1be03000-0000-0000-0000-000000000008"), "Giải quyết vấn đề & thuật toán", 8, true, "BE", "Đánh đổi giữa chi phí vận hành và độ phức tạp kỹ thuật", "vi", "Senior", "top1.be.senior.ops-cost-vs-complexity", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000001"), "System design & databases", 1, true, "BE", "Designing the architecture for a multi-service system", "en", "Senior", "top1.be.senior.multi-service-architecture", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "BE", "Mechanisms for keeping data in sync across services", "en", "Senior", "top1.be.senior.data-sync-across-services", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "BE", "Idempotency when designing an API or service", "en", "Senior", "top1.be.senior.idempotency", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000004"), "Technical depth", 4, true, "BE", "Retry and backoff strategy when calling another service", "en", "Senior", "top1.be.senior.retry-backoff", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000005"), "System design & databases", 5, true, "BE", "Weighing storage-model trade-offs at large scale", "en", "Senior", "top1.be.senior.storage-tradeoff-at-scale", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000006"), "Problem solving", 6, true, "BE", "Handling a production incident", "en", "Senior", "top1.be.senior.production-incident", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000007"), "System design & databases", 7, true, "BE", "Designing a fault-tolerant system", "en", "Senior", "top1.be.senior.fault-tolerant-design", 1 },
                    { new Guid("1be03011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "BE", "Trading off operating cost against technical complexity", "en", "Senior", "top1.be.senior.ops-cost-vs-complexity", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000001"), "Ý thức UI/UX & accessibility", 1, true, "FE", "HTML semantic: chọn đúng thẻ ngữ nghĩa cho một đoạn nội dung", "vi", "Fresher", "top1.fe.fresher.semantic-html", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "FE", "CSS box model: margin, border, padding hoạt động thế nào", "vi", "Fresher", "top1.fe.fresher.box-model", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000003"), "Ý thức UI/UX & accessibility", 3, true, "FE", "Căn giữa một phần tử bằng flexbox", "vi", "Fresher", "top1.fe.fresher.flexbox-center", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000004"), "Chiều sâu kỹ thuật", 4, true, "FE", "Khác nhau giữa let và const trong JavaScript", "vi", "Fresher", "top1.fe.fresher.let-vs-const", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000005"), "Chiều sâu kỹ thuật", 5, true, "FE", "Viết một hàm JavaScript xử lý dữ liệu đơn giản", "vi", "Fresher", "top1.fe.fresher.simple-function", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000006"), "Chiều sâu kỹ thuật", 6, true, "FE", "Thao tác DOM: chọn và đổi nội dung một phần tử", "vi", "Fresher", "top1.fe.fresher.dom-manipulation", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000007"), "Giải quyết vấn đề", 7, true, "FE", "Gọi một API bằng fetch và đọc kết quả trả về", "vi", "Fresher", "top1.fe.fresher.fetch-api", 1 },
                    { new Guid("1fe00000-0000-0000-0000-000000000008"), "Giải quyết vấn đề", 8, true, "FE", "Xử lý khi một phần tử không hiển thị đúng như mong đợi", "vi", "Fresher", "top1.fe.fresher.element-not-showing", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000001"), "UI/UX & accessibility awareness", 1, true, "FE", "Semantic HTML: picking the right tag for a piece of content", "en", "Fresher", "top1.fe.fresher.semantic-html", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "FE", "The CSS box model: how margin, border, and padding work", "en", "Fresher", "top1.fe.fresher.box-model", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000003"), "UI/UX & accessibility awareness", 3, true, "FE", "Centering an element with flexbox", "en", "Fresher", "top1.fe.fresher.flexbox-center", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000004"), "Technical depth", 4, true, "FE", "The difference between let and const in JavaScript", "en", "Fresher", "top1.fe.fresher.let-vs-const", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000005"), "Technical depth", 5, true, "FE", "Writing a simple JavaScript function to process data", "en", "Fresher", "top1.fe.fresher.simple-function", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000006"), "Technical depth", 6, true, "FE", "DOM manipulation: selecting and changing an element's content", "en", "Fresher", "top1.fe.fresher.dom-manipulation", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "FE", "Calling an API with fetch and reading the response", "en", "Fresher", "top1.fe.fresher.fetch-api", 1 },
                    { new Guid("1fe00011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "FE", "Handling a case where an element does not display as expected", "en", "Fresher", "top1.fe.fresher.element-not-showing", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000001"), "Chiều sâu kỹ thuật", 1, true, "FE", "Dựng UI hoàn chỉnh cho một tính năng bằng framework", "vi", "Junior", "top1.fe.junior.full-feature-ui", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "FE", "Quản lý state cục bộ trong component", "vi", "Junior", "top1.fe.junior.local-state", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "FE", "Xử lý form và validate dữ liệu nhập", "vi", "Junior", "top1.fe.junior.form-validate", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000004"), "Chiều sâu kỹ thuật", 4, true, "FE", "Gọi API bất đồng bộ và xử lý trạng thái loading/error", "vi", "Junior", "top1.fe.junior.async-loading-error", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000005"), "Ý thức UI/UX & accessibility", 5, true, "FE", "Xử lý layout vỡ hoặc chưa responsive", "vi", "Junior", "top1.fe.junior.responsive-broken-layout", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000006"), "Giải quyết vấn đề", 6, true, "FE", "Chẩn đoán vì sao một component re-render không cần thiết", "vi", "Junior", "top1.fe.junior.unnecessary-rerender", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000007"), "Giải quyết vấn đề", 7, true, "FE", "Chẩn đoán dữ liệu hiển thị sai do race condition khi gọi API", "vi", "Junior", "top1.fe.junior.race-condition-display", 1 },
                    { new Guid("1fe01000-0000-0000-0000-000000000008"), "Ý thức UI/UX & accessibility", 8, true, "FE", "Sửa một lỗi UI thường gặp trên nhiều kích thước màn hình", "vi", "Junior", "top1.fe.junior.cross-device-ui-fix", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000001"), "Technical depth", 1, true, "FE", "Building a complete UI for one feature with a framework", "en", "Junior", "top1.fe.junior.full-feature-ui", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "FE", "Managing local state inside a component", "en", "Junior", "top1.fe.junior.local-state", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "FE", "Handling forms and validating input", "en", "Junior", "top1.fe.junior.form-validate", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000004"), "Technical depth", 4, true, "FE", "Calling an API asynchronously and handling loading/error states", "en", "Junior", "top1.fe.junior.async-loading-error", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000005"), "UI/UX & accessibility awareness", 5, true, "FE", "Fixing a broken or non-responsive layout", "en", "Junior", "top1.fe.junior.responsive-broken-layout", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000006"), "Problem solving", 6, true, "FE", "Diagnosing why a component re-renders unnecessarily", "en", "Junior", "top1.fe.junior.unnecessary-rerender", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "FE", "Diagnosing data shown incorrectly because of a race condition when calling an API", "en", "Junior", "top1.fe.junior.race-condition-display", 1 },
                    { new Guid("1fe01011-0000-0000-0000-000000000008"), "UI/UX & accessibility awareness", 8, true, "FE", "Fixing a common UI bug across different screen sizes", "en", "Junior", "top1.fe.junior.cross-device-ui-fix", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000001"), "Chiều sâu kỹ thuật", 1, true, "FE", "Thiết kế cấu trúc component tái sử dụng", "vi", "Middle", "top1.fe.middle.reusable-component-structure", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000002"), "Giải quyết vấn đề", 2, true, "FE", "Chọn giải pháp quản lý state toàn cục phù hợp", "vi", "Middle", "top1.fe.middle.global-state-choice", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "FE", "Tối ưu hiệu năng render bằng memoization", "vi", "Middle", "top1.fe.middle.memoization", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000004"), "Chiều sâu kỹ thuật", 4, true, "FE", "Tối ưu hiệu năng render bằng lazy-load", "vi", "Middle", "top1.fe.middle.lazy-load", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000005"), "Ý thức UI/UX & accessibility", 5, true, "FE", "Đảm bảo accessibility cho một giao diện", "vi", "Middle", "top1.fe.middle.accessibility", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000006"), "Chiều sâu kỹ thuật", 6, true, "FE", "Viết test cho một component", "vi", "Middle", "top1.fe.middle.component-testing", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000007"), "Giải quyết vấn đề", 7, true, "FE", "Quyết định khi nào nên tách nhỏ một component", "vi", "Middle", "top1.fe.middle.split-component-decision", 1 },
                    { new Guid("1fe02000-0000-0000-0000-000000000008"), "Giải quyết vấn đề", 8, true, "FE", "Gỡ một vấn đề hiệu năng thực tế trên giao diện", "vi", "Middle", "top1.fe.middle.real-performance-issue", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000001"), "Technical depth", 1, true, "FE", "Designing a reusable component structure", "en", "Middle", "top1.fe.middle.reusable-component-structure", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000002"), "Problem solving", 2, true, "FE", "Choosing a suitable global state-management solution", "en", "Middle", "top1.fe.middle.global-state-choice", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "FE", "Optimizing render performance with memoization", "en", "Middle", "top1.fe.middle.memoization", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000004"), "Technical depth", 4, true, "FE", "Optimizing render performance with lazy-loading", "en", "Middle", "top1.fe.middle.lazy-load", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000005"), "UI/UX & accessibility awareness", 5, true, "FE", "Making an interface accessible", "en", "Middle", "top1.fe.middle.accessibility", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000006"), "Technical depth", 6, true, "FE", "Writing tests for a component", "en", "Middle", "top1.fe.middle.component-testing", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "FE", "Deciding when to split a component into smaller pieces", "en", "Middle", "top1.fe.middle.split-component-decision", 1 },
                    { new Guid("1fe02011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "FE", "Fixing a real-world performance issue in the UI", "en", "Middle", "top1.fe.middle.real-performance-issue", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000001"), "Chiều sâu kỹ thuật", 1, true, "FE", "Thiết kế kiến trúc micro-frontend cho hệ thống lớn", "vi", "Senior", "top1.fe.senior.micro-frontend-architecture", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000002"), "Chiều sâu kỹ thuật", 2, true, "FE", "Module federation: khái niệm và tình huống áp dụng", "vi", "Senior", "top1.fe.senior.module-federation", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000003"), "Chiều sâu kỹ thuật", 3, true, "FE", "Chiến lược caching và CDN cho frontend", "vi", "Senior", "top1.fe.senior.caching-cdn-strategy", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000004"), "Giải quyết vấn đề", 4, true, "FE", "Chuẩn hoá quy trình build và deploy cho nhiều team", "vi", "Senior", "top1.fe.senior.build-deploy-standard", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000005"), "Giải quyết vấn đề", 5, true, "FE", "Xử lý sự cố hiệu năng ở production", "vi", "Senior", "top1.fe.senior.production-performance-incident", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000006"), "Ý thức UI/UX & accessibility", 6, true, "FE", "Đánh đổi giữa trải nghiệm người dùng và chi phí kỹ thuật", "vi", "Senior", "top1.fe.senior.ux-vs-technical-cost", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000007"), "Giải quyết vấn đề", 7, true, "FE", "Đảm bảo khả năng bảo trì frontend ở quy mô nhiều team", "vi", "Senior", "top1.fe.senior.maintainability-multi-team", 1 },
                    { new Guid("1fe03000-0000-0000-0000-000000000008"), "Giải quyết vấn đề", 8, true, "FE", "Dẫn dắt kỹ thuật và chuẩn hoá cách làm cho nhiều team", "vi", "Senior", "top1.fe.senior.tech-leadership-standardization", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000001"), "Technical depth", 1, true, "FE", "Designing a micro-frontend architecture for a large system", "en", "Senior", "top1.fe.senior.micro-frontend-architecture", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000002"), "Technical depth", 2, true, "FE", "Module federation: the concept and when to use it", "en", "Senior", "top1.fe.senior.module-federation", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000003"), "Technical depth", 3, true, "FE", "Caching and CDN strategy for a frontend", "en", "Senior", "top1.fe.senior.caching-cdn-strategy", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000004"), "Problem solving", 4, true, "FE", "Standardizing the build and deploy process across teams", "en", "Senior", "top1.fe.senior.build-deploy-standard", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000005"), "Problem solving", 5, true, "FE", "Handling a production performance incident", "en", "Senior", "top1.fe.senior.production-performance-incident", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000006"), "UI/UX & accessibility awareness", 6, true, "FE", "Trading off user experience against technical cost", "en", "Senior", "top1.fe.senior.ux-vs-technical-cost", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000007"), "Problem solving", 7, true, "FE", "Ensuring frontend maintainability at multi-team scale", "en", "Senior", "top1.fe.senior.maintainability-multi-team", 1 },
                    { new Guid("1fe03011-0000-0000-0000-000000000008"), "Problem solving", 8, true, "FE", "Providing technical leadership and standardizing practices across teams", "en", "Senior", "top1.fe.senior.tech-leadership-standardization", 1 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba00011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba01011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba02011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1ba03011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be00011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be01011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be02011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1be03011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe00011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe01011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe02011-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "practice_topics",
                keyColumn: "id",
                keyValue: new Guid("1fe03011-0000-0000-0000-000000000008"));
        }
    }
}
