-- Fixture cho phép đo "decide-next có trả về ID tiêu chí đọc được không".
--
-- Chỉ ĐỌC. Dựng lại đúng đầu vào mà `/decide-next` đã nhận trên production cho một lượt trả lời:
-- câu hỏi hiện tại, bản chép, lịch sử chuỗi, và ẢNH CHỤP TRẠNG THÁI BẰNG CHỨNG của buổi đó.
--
-- Vì sao cần: log production cho thấy model trả `targetCriterionId='Giao tiếp & trình bày'` — TÊN
-- chứ không phải GUID — nên .NET bỏ qua mọi cập nhật, và `session_criterion_evidence` đứng
-- UNKNOWN vĩnh viễn (178 dòng UNKNOWN, 0 dòng SATISFIED). Phép đo này đếm tỉ lệ model trả đúng
-- GUID, để so trước/sau khi siết prompt — thay vì tin là đã sửa được.
select json_agg(row_to_json(t))
from (
  select
    a.id::text                              as answer_id,
    q.content                               as current_question,
    a.transcript                            as transcript,
    ps.job_category::text                   as job_category,
    ps.language                             as language,
    ps.seniority                            as seniority,
    q.depth                                 as current_depth,
    ps.max_deep_per_question                as max_depth,
    ps.max_questions                        as max_questions,
    ps.max_follow_ups                       as max_follow_ups,

    -- Câu gốc của chuỗi (mỏ neo chủ đề) — `AnswerService` gửi trường này ở chế độ chuỗi.
    (select r.content from practice_questions r
      where r.id = coalesce(q.root_question_id, q.id))   as root_question,

    -- Lịch sử chuỗi: các lượt Q&A TRƯỚC câu hiện tại trong cùng chuỗi, theo thứ tự hội thoại.
    (
      select json_agg(json_build_object(
               'question', h.content,
               'answer',   coalesce(ha.transcript, ''),
               'kind',     lower(h.kind::text)) order by h.order_no)
      from practice_questions h
      left join practice_answers ha on ha.question_id = h.id
      where h.session_id = ps.id
        and coalesce(h.root_question_id, h.id) = coalesce(q.root_question_id, q.id)
        and h.order_no < q.order_no
    )                                       as history,

    -- Bộ tiêu chí đúng như đường chấm nạp (chỉ cần name + description cho prompt).
    (
      select json_agg(json_build_object('name', c.name, 'description', c.description) order by c.name)
      from session_criterion_evidence e2
      join rubric_criteria c on c.id = e2.criterion_id
      where e2.session_id = ps.id
    )                                       as criteria,

    -- ẢNH CHỤP BẰNG CHỨNG — thứ đang được đo. `criterionId` là GUID; model phải trả lại ĐÚNG
    -- một trong các giá trị này, chứ không phải `name` bên cạnh nó.
    (
      select json_agg(json_build_object(
               'criterionId',     e.criterion_id::text,
               'name',            e.criterion_name,
               'state',           e.state,
               'evidenceFound',   coalesce(e.evidence_found, '{}'),
               'missingEvidence', coalesce(e.missing_evidence, '{}'),
               'deepCount',       e.deep_count) order by e.criterion_name)
      from session_criterion_evidence e where e.session_id = ps.id
    )                                       as current_evidence_state

  from practice_answers a
  join practice_questions q  on q.id  = a.question_id
  join practice_sessions  ps on ps.id = a.session_id
  where a.transcript is not null
    and length(btrim(a.transcript)) >= 40
    and ps.campaign_id is null
    and exists (select 1 from session_criterion_evidence e3 where e3.session_id = ps.id)
  order by a.id
) t;
