"""Regression tests for cc-outlook CLI behavior.

Covers the recipients command: it now exposes a boolean --json (consistent with
every other JSON-capable command in both email tools) instead of a
--format table|json option, and emits ASCII-only JSON (ensure_ascii=True).

Also covers the attachment commands (issue #2539): download-attachment must
report the path it actually wrote and must exit non-zero when nothing was
written, and the attachments listing must show an attachment id in full.
"""

import json
from unittest.mock import MagicMock, patch

from typer.testing import CliRunner

from src.cli import app

runner = CliRunner()

# Wider than any terminal, which is what made this id unusable inside a table cell.
LONG_ATTACHMENT_ID = "AAMkAG" + "Q" * 160 + "=="


def _patch_client(recipients):
    client = MagicMock()
    client.get_all_recipients.return_value = recipients
    return patch("src.cli.get_client", return_value=client)


def _extract_json(output):
    # The command prints a progress line to stderr before the JSON array; the
    # JSON payload starts at the first '['.
    idx = output.index("[")
    return output[idx:]


class TestRecipientsJson:
    def test_json_flag_outputs_ascii_json(self):
        recipients = {
            "jose@example.com": {"name": "Jos\xe9", "sent_count": 3},
        }
        with _patch_client(recipients):
            result = runner.invoke(app, ["recipients", "--json"])
        assert result.exit_code == 0, result.output
        segment = _extract_json(result.output)
        # Must be valid JSON and pure ASCII (the non-ASCII name is escaped).
        payload = json.loads(segment)
        assert payload[0]["email"] == "jose@example.com"
        segment.encode("ascii")

    def test_format_option_no_longer_accepted(self):
        with _patch_client({}):
            result = runner.invoke(app, ["recipients", "--format", "json"])
        # --format was removed; passing it is now an error.
        assert result.exit_code != 0


def _patch_attachment_client(atts, download=None, download_error=None):
    client = MagicMock()
    client.list_attachments.return_value = atts
    if download_error is not None:
        client.download_attachment.side_effect = download_error
    else:
        client.download_attachment.return_value = download
    return patch("src.cli.get_client", return_value=client)


_ATTACHMENT = {
    "id": LONG_ATTACHMENT_ID,
    "name": "invite.ics",
    "size": 2048,
    "content_type": "text/calendar",
    "is_inline": False,
}


class TestAttachmentsListing:
    """Issue #2539, secondary defect: the id could not be read out of the output.

    The id was a column in a Rich table, so it was ellipsized at terminal width -
    and it is the one value download-attachment requires.
    """

    def test_full_id_is_printed(self):
        with _patch_attachment_client([_ATTACHMENT]):
            result = runner.invoke(app, ["attachments", "msg-1"])
        assert result.exit_code == 0, result.output
        # Not merely a prefix: every character, unbroken, on one line.
        assert LONG_ATTACHMENT_ID in result.output
        assert "..." not in result.output

    def test_json_flag_emits_the_attachment_records(self):
        with _patch_attachment_client([_ATTACHMENT]):
            result = runner.invoke(app, ["attachments", "msg-1", "--json"])
        assert result.exit_code == 0, result.output
        payload = json.loads(result.output)
        assert payload[0]["id"] == LONG_ATTACHMENT_ID
        assert payload[0]["name"] == "invite.ics"


class TestDownloadAttachmentCommand:
    """Issue #2539: a green success line and exit 0 while writing no file."""

    def test_reports_the_path_that_was_written(self):
        written = {"name": "invite.ics", "path": "C:/out/invite.ics", "size": 12}
        with _patch_attachment_client([_ATTACHMENT], download=written):
            result = runner.invoke(
                app, ["download-attachment", "msg-1", LONG_ATTACHMENT_ID, "-o", "C:/out/asked.ics"]
            )
        assert result.exit_code == 0, result.output
        # The path WRITTEN, not the path asked for.
        assert "C:/out/invite.ics" in result.output
        assert "asked.ics" not in result.output

    def test_failed_write_exits_non_zero(self):
        with _patch_attachment_client(
            [_ATTACHMENT], download_error=RuntimeError("Failed to write attachment")
        ):
            result = runner.invoke(
                app, ["download-attachment", "msg-1", LONG_ATTACHMENT_ID, "-o", "C:/out/invite.ics"]
            )
        # The whole point of the bug: this used to be 0 with a green success line.
        assert result.exit_code != 0
        assert "Downloaded" not in result.output
        assert "Failed to write attachment" in result.output
