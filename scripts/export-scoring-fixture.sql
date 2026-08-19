-- WS-0 — xuất fixture chấm lại cho scripts/benchmark-scoring.py
--
-- Chỉ ĐỌC. Dựng lại ĐÚNG đầu vào mà `provider.score()` đã nhận trên production cho từng answer:
--   • question / transcript / language / jobCategory
--   • bộ tiêu chí ĐÃ THẬT SỰ chấm câu đó — lấy từ `answer_scores.criterion_id` chứ không suy lại
--     luật `ScoringScopeFilter`, để fixture không thể trôi khỏi thứ đã xảy ra
--   • chỉ số cách nói F11 (khớp `DeliveryMetrics.to_dict()`, camelCase)
--   • điểm baseline để so lệch
--
-- `levels` KHÔNG xuất ở đây: `rubric_levels` đang rỗng toàn bộ nên production đi nhánh
-- `ScoringCriteriaBuilder.DefaultBand(maxScore)` — script tự dựng lại dải 0..maxScore.
-- Khi WS-D nạp mức thật thì thêm cột levels vào đây và bỏ nhánh dựng lại trong script.
select json_agg(row_to_json(t))
from (
  select
    a.id::text                              as answer_id,
    q.content                               as question,
    a.transcript                            as transcript,
    ps.job_category::text                   as job_category,
    ps.language                             as language,
    max(sc.rubric_version)                  as rubric_version,

    -- Chỉ số cách nói: NULL toàn bộ ⇒ trả null để script truyền delivery=None
    -- (prompt sẽ dùng khối "chưa đo được", đúng như production đã làm cho câu đó).
    case when a.audio_sec is null then null else json_build_object(
      'metricsVersion',    a.metrics_version,
      'audioSec',          a.audio_sec,
      'speechSec',         a.speech_sec,
      'wordCount',         a.word_count,
      'speechRateWpm',     a.speech_rate_wpm,
      'longestPauseSec',   a.longest_pause_sec,
      'pauseCount',        a.pause_count,
      'silenceRatio',      a.silence_ratio,
      'fillerCount',       a.filler_count,
      'fillerPer100Words', a.filler_per100words,
      -- filler_breakdown lưu dạng TEXT (ValueConverter phía EF), không phải jsonb — phải cast
      -- tường minh, nếu không `json_build_object` nhét nguyên chuỗi vào làm string lồng string.
      'fillerBreakdown',   coalesce(a.filler_breakdown, '{}')::json
    ) end                                   as delivery,

    (
      select json_agg(json_build_object(
               'criterionId', c.id::text,
               'name',        c.name,
               'description', c.description,
               'maxScore',    c.max_score,
               'weight',      c.weight
             ) order by c.name)
      from (select distinct criterion_id from answer_scores where answer_id = a.id) d
      join rubric_criteria c on c.id = d.criterion_id
    )                                       as criteria,

    -- Điểm baseline = median qua các attempt (E10), khớp cách .NET chốt điểm.
    -- SelfConsistencyN đang là 1 nên median-of-1, nhưng viết đúng công thức để khỏi lệch nếu bật.
    (
      select json_agg(json_build_object('criterionId', criterion_id::text, 'score', med)
                      order by criterion_id)
      from (
        select criterion_id,
               percentile_cont(0.5) within group (order by score) as med
        from answer_scores where answer_id = a.id group by criterion_id
      ) m
    )                                       as baseline_scores

  from practice_answers a
  join practice_questions q  on q.id  = a.question_id
  join practice_sessions  ps on ps.id = a.session_id
  join answer_scores      sc on sc.answer_id = a.id
  where a.status = 'Scored'
    and a.transcript is not null
    and length(btrim(a.transcript)) >= 20   -- bỏ bản chép cụt: chấm lại chúng chỉ đo nhiễu
    and ps.campaign_id is null              -- B2C, đúng nhóm người dùng đang than chậm
  group by a.id, q.content, a.transcript, ps.job_category, ps.language,
           a.audio_sec, a.speech_sec, a.word_count, a.speech_rate_wpm,
           a.longest_pause_sec, a.pause_count, a.silence_ratio, a.filler_count,
           a.filler_per100words, a.filler_breakdown, a.metrics_version
  order by a.id
) t;
