"""Tests for the cc-devthrottle email owner verb (issue #1318 consumer)."""

import base64
import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()


def _run_owner(extra_args, send_return=None):
    with patch("src.email_ops.EmailClient") as client_cls:
        instance = client_cls.return_value
        instance.send_owner.return_value = send_return if send_return is not None else {"sent": True}
        result = runner.invoke(app, ["email", "owner"] + extra_args)
        call = instance.send_owner.call_args
    return result, call


def test_owner_send_passes_subject_and_body():
    result, call = _run_owner(
        ["--subject", "Nightly drift", "--body", "2 aging PRs"],
        send_return={"sent": True, "providerId": "resend-1"},
    )
    assert result.exit_code == 0
    # send_owner(subject, body_text, body_html, attachments)
    assert call.args[0] == "Nightly drift"
    assert call.args[1] == "2 aging PRs"
    assert call.args[3] == []  # no attachments
    assert "Sent" in result.output


def test_owner_has_no_recipient_option():
    result = runner.invoke(app, ["email", "owner", "--help"])
    assert result.exit_code == 0
    assert "--subject" in result.output
    assert "--attach" in result.output
    assert "--to" not in result.output
    assert "--recipient" not in result.output


def test_owner_requires_a_body_or_attachment():
    result, call = _run_owner(["--subject", "Only a subject"])
    assert result.exit_code != 0
    assert call is None
    assert "body" in result.output.lower()


def test_owner_html_body_is_forwarded():
    result, call = _run_owner(["--subject", "Report", "--html", "<p>drift</p>"])
    assert result.exit_code == 0
    assert call.args[1] is None       # no plain text
    assert call.args[2] == "<p>drift</p>"


def test_owner_attaches_file_as_base64(tmp_path):
    report = tmp_path / "report.html"
    report.write_text("<html><body>offline report</body></html>", encoding="utf-8")

    result, call = _run_owner(["--subject", "Report", "--attach", str(report)])
    assert result.exit_code == 0
    attachments = call.args[3]
    assert len(attachments) == 1
    a = attachments[0]
    assert a["filename"] == "report.html"
    assert a["contentType"] == "text/html"
    decoded = base64.b64decode(a["content"]).decode("utf-8")
    assert decoded == "<html><body>offline report</body></html>"


def test_owner_missing_attachment_fails_clearly(tmp_path):
    missing = tmp_path / "nope.html"
    result, call = _run_owner(["--subject", "Report", "--attach", str(missing)])
    assert result.exit_code != 0
    assert "not found" in result.output.lower()
