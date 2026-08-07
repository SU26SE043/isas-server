import logging

from app.language import VI, normalize


def test_english_is_downgraded_with_a_deploy_warning(monkeypatch, caplog):
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi")

    with caplog.at_level(logging.WARNING):
        assert normalize("en") == VI

    assert "downgraded to Vietnamese" in caplog.text
