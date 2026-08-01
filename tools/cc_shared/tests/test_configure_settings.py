"""The cc-settings-api client resolves its Director - and that Director's credential - per instance.

The script under test lives in .claude/skills/cc-settings-api/ (outside any package), so it is
loaded by file path. What these tests pin is the re-inspection's P1 finding: discovery read
registrations only from the flat storage base, and the credential only from flat-base files, so on
a clean install - where every Director's storage lives under <base>/instances/<slug> - the script
found no Director at all, and on a machine with a stale flat token it could present a credential
the discovered Director does not accept. Discovery now hands back the HOME with the endpoint, and
the credential is read from that home, so the two cannot name different Directors.
"""

import importlib.util
import json
import os
import sys
from pathlib import Path

import pytest

_REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent
_SCRIPT = _REPO_ROOT / ".claude" / "skills" / "cc-settings-api" / "configure_settings.py"


@pytest.fixture()
def settings():
    spec = importlib.util.spec_from_file_location("configure_settings_under_test", _SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _pin_machine_root(monkeypatch, tmp_path) -> Path:
    monkeypatch.delenv("CC_DIRECTOR_ROOT", raising=False)
    if sys.platform == "win32":
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        return tmp_path / "cc-director"
    monkeypatch.setenv("HOME", str(tmp_path))
    return tmp_path / ".local" / "share" / "cc-director"


def _write_registration(home: Path, *, port: int, pid: int, started: str):
    reg_dir = home / "config" / "director" / "instances"
    reg_dir.mkdir(parents=True, exist_ok=True)
    (reg_dir / f"d{port}.json").write_text(json.dumps({
        "DirectorId": f"d{port}",
        "Pid": pid,
        "ControlEndpoint": f"http://127.0.0.1:{port}",
        "StartedAt": started,
    }), encoding="utf-8")


def _write_secret(home: Path, secret: str):
    d = home / "config" / "director"
    d.mkdir(parents=True, exist_ok=True)
    (d / "gateway-token.txt").write_text(secret, encoding="utf-8")


def test_discovery_finds_the_clean_installs_default_instance_and_its_home(settings, tmp_path, monkeypatch):
    """The clean named-default layout: no flat files at all. The old discovery raised
    'No Director instances directory' here while a Director was demonstrably running."""
    shared = _pin_machine_root(monkeypatch, tmp_path)
    home = shared / "instances" / "default"
    _write_registration(home, port=7883, pid=os.getpid(), started="2026-07-31T01:00:00Z")
    _write_secret(home, "the-default-instances-secret")

    endpoint, found_home = settings.discover_director()

    assert endpoint == "http://127.0.0.1:7883"
    assert found_home == home
    assert settings._resolve_token(None, found_home) == "the-default-instances-secret"


def test_the_credential_comes_from_the_discovered_instances_home_not_the_flat_base(settings, tmp_path, monkeypatch):
    """A stale flat-base token beside a live named instance: the credential presented must be the
    one the DISCOVERED Director accepts."""
    shared = _pin_machine_root(monkeypatch, tmp_path)
    _write_secret(shared, "a-stale-flat-base-token")
    home = shared / "instances" / "blue"
    _write_registration(home, port=7900, pid=os.getpid(), started="2026-07-31T01:00:00Z")
    _write_secret(home, "the-blue-instances-secret")

    endpoint, found_home = settings.discover_director()

    assert endpoint == "http://127.0.0.1:7900"
    assert settings._resolve_token(None, found_home) == "the-blue-instances-secret"


def test_the_newest_live_director_wins_across_homes(settings, tmp_path, monkeypatch):
    """Two live Directors in different homes: the newest StartedAt is the one addressed, and the
    home returned is that same Director's - never a mix of one's endpoint with the other's secret."""
    shared = _pin_machine_root(monkeypatch, tmp_path)
    older = shared / "instances" / "default"
    newer = shared / "instances" / "blue"
    _write_registration(older, port=7883, pid=os.getpid(), started="2026-07-31T01:00:00Z")
    _write_secret(older, "older-secret")
    _write_registration(newer, port=7900, pid=os.getpid(), started="2026-07-31T02:00:00Z")
    _write_secret(newer, "newer-secret")

    endpoint, found_home = settings.discover_director()

    assert endpoint == "http://127.0.0.1:7900"
    assert settings._resolve_token(None, found_home) == "newer-secret"


def test_a_pre_instance_install_is_still_discovered_at_the_flat_base(settings, tmp_path, monkeypatch):
    shared = _pin_machine_root(monkeypatch, tmp_path)
    _write_registration(shared, port=7880, pid=os.getpid(), started="2026-07-31T01:00:00Z")
    _write_secret(shared, "the-flat-base-secret")

    endpoint, found_home = settings.discover_director()

    assert endpoint == "http://127.0.0.1:7880"
    assert settings._resolve_token(None, found_home) == "the-flat-base-secret"


def test_no_running_director_names_every_directory_it_searched(settings, tmp_path, monkeypatch):
    shared = _pin_machine_root(monkeypatch, tmp_path)
    (shared / "instances" / "default").mkdir(parents=True)

    with pytest.raises(RuntimeError) as caught:
        settings.discover_director()

    message = str(caught.value)
    assert "no running Director" in message
    assert str(shared) in message
