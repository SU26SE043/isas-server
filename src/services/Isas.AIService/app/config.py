from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    gemini_api_key: str
    gemini_model: str = "gemini-2.5-flash"
    question_count: int = 5

    # Whisper
    whisper_model: str = "large-v3"
    whisper_device: str = "cpu"
    whisper_compute_type: str = "int8"

    # ── RABBITMQ CONFIG ──────────────────────────────────────────
    rabbitmq_url: str = "amqp://guest:guest@localhost/"
    queue_name: str = "scoring_pipeline_queue"   # TRÙNG RabbitMQ:QueueName .NET

    # ── S3 / SEAWEEDFS CONFIG ────────────────────────────────────
    s3_endpoint: str = "http://localhost:8333"
    s3_access_key: str = "your-access-key"
    s3_secret_key: str = "your-secret-key"
    s3_bucket: str = "isas-files"

    # ── CALLBACK VỀ .NET ─────────────────────────────────────────
    dotnet_callback_base: str = "http://localhost:5246"  # cổng .NET API
    internal_token: str = "change-me"                    # trùng Internal:Token .NET


settings = Settings()