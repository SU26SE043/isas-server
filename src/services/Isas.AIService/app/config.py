from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    gemini_api_key: str
    gemini_model: str = "gemini-2.5-flash"
    question_count: int = 5

    # Whisper
    whisper_model: str = "large-v3"        # tiny | base | small | medium | large-v3
    whisper_device: str = "cpu"         # Mac dùng cpu
    whisper_compute_type: str = "int8"  # int8 cho CPU (nhẹ, nhanh)

settings = Settings()