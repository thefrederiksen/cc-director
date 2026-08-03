"""Tests for cc-devthrottle automation-browser operations.

Focus: the attach output MUST be exactly two eval-able export lines (so
`eval "$(cc-devthrottle browser attach 'Name')"` points browser-harness at the browser), and target
resolution accepts a name or an id and fails loudly on an unknown target.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

import typer  # noqa: E402
from src import browser_ops  # noqa: E402


def _stub_director(monkeypatch, browsers, attach=None):
    """Point browser_ops.gateway at canned responses keyed by request path.

    Remove-the-network-port mission, phase 2: the browsers now hang off the Gateway's
    /directors/{id}/browsers, addressed to the Director this session belongs to. The env var is set
    here because that id is how "this machine" is named once the loopback port is gone.
    """
    monkeypatch.setenv("CC_DIRECTOR_ID", "dir-1")

    def fake_get_json(path):
        if path == "directors/dir-1/browsers":
            return {"browsers": browsers}
        if path.endswith("/attach"):
            return attach
        raise AssertionError(f"unexpected GET {path}")

    monkeypatch.setattr(browser_ops.gateway, "get_json", fake_get_json)


SAMPLE = [
    {
        "id": "center-consulting",
        "name": "Center Consulting",
        "browser": "Chrome",
        "port": 9310,
        "status": "Ready",
        "statusLabel": "Ready",
        "account": "soren@centerconsulting.com",
        "buName": "center-consulting",
        "buCdpUrl": "http://127.0.0.1:9310",
    }
]


class TestAttach:
    def test_prints_exactly_two_export_lines(self, monkeypatch, capsys):
        _stub_director(
            monkeypatch,
            SAMPLE,
            attach={"buName": "center-consulting", "buCdpUrl": "http://127.0.0.1:9310"},
        )

        browser_ops.attach_browser("Center Consulting")

        out = capsys.readouterr().out.strip().splitlines()
        assert out == [
            "export BU_NAME=center-consulting",
            "export BU_CDP_URL=http://127.0.0.1:9310",
        ]

    def test_resolves_by_id_too(self, monkeypatch, capsys):
        _stub_director(
            monkeypatch,
            SAMPLE,
            attach={"buName": "center-consulting", "buCdpUrl": "http://127.0.0.1:9310"},
        )

        browser_ops.attach_browser("center-consulting")

        out = capsys.readouterr().out
        assert "export BU_NAME=center-consulting" in out
        assert "export BU_CDP_URL=http://127.0.0.1:9310" in out


class TestResolve:
    def test_unknown_target_exits_nonzero(self, monkeypatch):
        _stub_director(monkeypatch, SAMPLE)
        with pytest.raises(typer.Exit) as exc:
            browser_ops._resolve("does-not-exist")
        assert exc.value.exit_code == 1

    def test_matches_name_case_insensitively(self, monkeypatch):
        _stub_director(monkeypatch, SAMPLE)
        resolved = browser_ops._resolve("center consulting")
        assert resolved["id"] == "center-consulting"


class TestStop:
    def test_posts_stop_and_reports_the_folded_status(self, monkeypatch, capsys):
        _stub_director(monkeypatch, SAMPLE)
        posted = {}

        def fake_post_json(path, body):
            posted["path"] = path
            posted["body"] = body
            return {"id": "center-consulting", "name": "Center Consulting", "statusLabel": "Stopped"}

        monkeypatch.setattr(browser_ops.gateway, "post_json", fake_post_json)

        browser_ops.stop_browser("Center Consulting", json_output=False)

        assert posted["path"] == "directors/dir-1/browsers/center-consulting/stop"
        out = capsys.readouterr().out
        assert "Stopped" in out
        assert "Center Consulting" in out


class TestList:
    def test_json_output_is_the_raw_browsers(self, monkeypatch, capsys):
        _stub_director(monkeypatch, SAMPLE)
        browser_ops.list_browsers(json_output=True)
        out = capsys.readouterr().out
        assert '"center-consulting"' in out
        assert '"soren@centerconsulting.com"' in out
