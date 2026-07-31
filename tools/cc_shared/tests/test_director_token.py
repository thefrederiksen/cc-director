"""The credential the command line derives, and the two ways it used to get it wrong.

The pinned values here are the SAME literals asserted on the C# side
(src/CcDirector.Core.Tests/Security/DirectorScopedTokenTests.cs). The command line mints in Python
and the Director verifies in C#, so if the two implementations ever drift, nothing else notices: each
suite would agree with its own language, and the only symptom would be every command line call on
every machine answered 401.
"""

import json
import sys
from pathlib import Path

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

import pytest  # noqa: E402

from cc_shared import director_token  # noqa: E402

SECRET = "the-machine-secret"


@pytest.mark.parametrize("scope,session_id,expected", [
    ("cli", None, "v1.cli..bb90uJEsFOMMSHAxvBx7VP1Ev5UKlG2nOFwY4Opib9w"),
    ("admin", None, "v1.admin..5RUdurdmGD5_jaXIBud_DeYKuUBsbI_wg1Ajy6DVFNg"),
    ("session-child", "11111111-1111-1111-1111-111111111111",
     "v1.session-child.11111111-1111-1111-1111-111111111111."
     "2-D4PpH-iMozeR6UpRlXExGKkUHuDj4NzimEZkH5w1g"),
])
def test_the_token_matches_what_the_director_verifies(scope, session_id, expected):
    assert director_token.mint(SECRET, scope, session_id) == expected


def test_a_child_token_must_name_its_session():
    with pytest.raises(ValueError):
        director_token.mint(SECRET, "session-child")


def test_a_full_scope_must_not_name_a_session():
    with pytest.raises(ValueError):
        director_token.mint(SECRET, "cli", "11111111-1111-1111-1111-111111111111")


def _write(root: Path, *, gateway_token=None, file_token=None):
    if gateway_token is not None:
        cfg = root / "config"
        cfg.mkdir(parents=True, exist_ok=True)
        (cfg / "config.json").write_text(json.dumps({"gateway": {"token": gateway_token}}), encoding="utf-8")
    if file_token is not None:
        d = root / "config" / "director"
        d.mkdir(parents=True, exist_ok=True)
        (d / "gateway-token.txt").write_text(file_token, encoding="utf-8")


def test_the_shared_fleet_token_wins_when_the_machine_is_attached_to_a_gateway(tmp_path, monkeypatch):
    """The bug this replaces: reading only the token FILE.

    That is correct on a standalone machine and wrong on every machine with a Gateway configured -
    the Director accepts the fleet token there, so the command line would derive from the wrong
    secret and be answered 401 on every call. Nothing could notice while the surface accepted
    everybody.
    """
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path))
    _write(tmp_path, gateway_token="the-fleet-token", file_token="this-machines-own-token")

    assert director_token.root_secret() == "the-fleet-token"


def test_the_machines_own_token_is_used_when_there_is_no_gateway(tmp_path, monkeypatch):
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path))
    _write(tmp_path, file_token="this-machines-own-token")

    assert director_token.root_secret() == "this-machines-own-token"


def test_a_machine_with_no_secret_says_so_rather_than_guessing(tmp_path, monkeypatch):
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path))

    assert director_token.root_secret() is None
    assert director_token.cli_token() is None


def test_the_storage_root_override_is_honoured(tmp_path, monkeypatch):
    """A named instance keeps its whole storage under its own home, and its Director's secret with
    it. Composing the path from LOCALAPPDATA - which is what this did - reads the DEFAULT instance's
    secret while talking to a named instance's Director."""
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path))

    assert director_token.storage_root() == tmp_path
    assert director_token.token_file_path().is_relative_to(tmp_path)
    assert director_token.config_json_path().is_relative_to(tmp_path)
