# ISAS — Cơ sở dữ liệu

- **PostgreSQL** (EF Core), quy ước cột **snake_case** (`UseSnakeCaseNamingConvention`).
- Enum lưu dưới dạng **string** (`HasConversion<string>`), không lưu số.
- Tách DB theo service: **`isas`** (Auth) và **`isas_interview`** (Interview) — `__EFMigrationsHistory` riêng.
- Tham chiếu user giữa service là **lỏng** (`candidate_id`/`user_id` là Guid, **không** FK xuyên service).

---

## 1. InterviewService DB (`isas_interview`)

### ER tóm tắt
```
practice_sessions 1──* practice_questions 1──1 practice_answers 1──* answer_scores
        │                                                              │
        └──*? file_records (cv_id, jd_id)            rubric_criteria ──┘
rubric_criteria 1──* rubric_levels 1──* rubric_anchors
```

### `practice_sessions`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| candidate_id | uuid | bắt buộc, **indexed**; ref lỏng tới AuthService |
| cv_id | uuid? | FK → file_records (Restrict), optional |
| jd_id | uuid? | FK → file_records (Restrict), optional |
| job_category | varchar(8) | enum string: `BA`/`BE`/`FE` |
| status | varchar(32) | enum string (xem [rules.md](rules.md)) |
| created_at | timestamptz | |
| completed_at | timestamptz? | set khi submit |

### `practice_questions`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| session_id | uuid | FK → practice_sessions (**Cascade**) |
| order_no | int | thứ tự câu hỏi |
| content | text | nội dung câu hỏi |
| time_limit_sec | int | mặc định 120 |
| created_at | timestamptz | |
| | | **unique (session_id, order_no)** |

### `practice_answers`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK (cũng dùng làm fileId của audio) |
| session_id | uuid | FK → practice_sessions (Cascade) |
| question_id | uuid | FK → practice_questions (1–1, Restrict) |
| audio_object_key | varchar(512)? | key audio trên SeaweedFS |
| transcript | text? | điền sau khi worker transcribe |
| status | varchar(32) | enum AnswerStatus |
| duration_sec | int | |
| created_at | timestamptz | |
| last_scoring_published_at | timestamptz? | mốc publish gần nhất (republisher dựa vào) |
| | | **unique (session_id, question_id)** — tối đa 1 answer/câu |

### `answer_scores`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| answer_id | uuid | FK → practice_answers (Cascade) |
| criterion_id | uuid | FK → rubric_criteria (Restrict) |
| attempt_no | int | mặc định 1 (mở đường self-consistency nhiều lần chấm) |
| score | numeric(5,2) | điểm tiêu chí |
| reasoning | text? | lý do chấm |
| rubric_version | int | version rubric lúc chấm |
| created_at | timestamptz | |
| | | **unique (answer_id, criterion_id, attempt_no)** |

### `rubric_criteria`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| name | varchar(128) | tên tiêu chí |
| description | text? | |
| weight | numeric(5,4) | trọng số tính điểm tổng |
| max_score | int | thang điểm tối đa (vd 5) |
| is_active | bool | rubric đang dùng |
| job_category | varchar(8) | enum string |
| version | int | mặc định 1 |
| | | **index (job_category, version, is_active)** |

### `rubric_levels`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| criterion_id | uuid | FK → rubric_criteria (Cascade) |
| score | int | mức điểm (0..max) |
| descriptor | text | mô tả mức điểm |
| | | **unique (criterion_id, score)** |

### `rubric_anchors`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| level_id | uuid | FK → rubric_levels (Cascade) |
| example_answer | text | câu trả lời mẫu cho mức điểm |

### `file_records`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| user_id | uuid | chủ file |
| file_type | varchar | `cv` / `jd` / `answer-audio` |
| original_name | varchar | tên gốc |
| storage_path | varchar | key trên SeaweedFS |
| storage_bucket | varchar | bucket (`isas-files`) |
| mime_type | varchar | |
| file_size | bigint | byte |
| parsed_text | text? | text trích từ PDF (CV/JD) |
| parse_status | varchar | `pending` / `done` / `failed` |
| created_at | timestamptz | |
| updated_at | timestamptz | |

---

## 2. AuthService DB (`isas`)

> Quản lý người dùng + token. Cấu trúc chi tiết theo code AuthService; ở mức tổng quan:

- **users** — tài khoản (email, hash mật khẩu, hồ sơ, liên kết Google OAuth).
- **refresh tokens** — refresh token cho cơ chế JWT (thời hạn `Jwt:RefreshTokenDays`).

JWT phát bởi Auth được InterviewService **validate** bằng cùng `Jwt:Key`/`Issuer`/`Audience` (xem [rules.md](rules.md) §xác thực).

---


