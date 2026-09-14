# BẢN SAO hợp đồng dây SC2 — phần Interview NHẬN (W4) + PHÁT (W5).
# Nguồn sự thật: `scratchpad/orchestra/contracts.md` của vòng SC2 (sha256 c60fbe7b00cc70de…), chép NGUYÊN VĂN
# hai mục W4/W5 vào test project để test khoá tên khoá JSON KHÔNG phụ thuộc repo Campaign hay file ngoài
# repo (file scratchpad không tồn tại trên CI). Đổi tên khoá ở đây = đổi hợp đồng ⇒ phải đổi ở CẢ HAI
# service, không phải "sửa cho test xanh". Test đọc: CampaignScoringScopeSc2Tests.Contract_*.

## W4 Campaign → Interview POST /internal/sessions/campaign  (JsonContent.Create — camelCase, đã verify)
criteria[].scoringScope : "Always" | "WhenTargeted"   — vắng ⇒ Always (Interview default).
questionDetails[].targetCriterionIds : Guid[] | null   — id campaign_criteria; Interview map → rubric_criteria.id qua source_criterion_id;
                                                       id không map được ⇒ BỎ + LogWarning; [] ⇒ giữ [] (chỉ Always); null ⇒ null (chấm đủ).
Interview: practice_sessions.scoring_scope_version = 2 khi có câu targetCriterionIds != null (kể cả []), else 1.

## W5 Interview → Campaign GET bộ chuẩn B2C (đường GetB2CRubricAsync)
B2CRubricApiCriterion += scoringScope : "Always" | "WhenTargeted".
