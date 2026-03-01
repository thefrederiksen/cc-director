"""Tests for cc-outlook authentication path functions and resolve_account."""

import json
from pathlib import Path
from unittest.mock import patch, MagicMock

import pytest


# ---------------------------------------------------------------------------
# We need to mock heavy third-party imports (msal, O365) before importing
# src.auth so the module loads without those packages actually installed in
# the test environment.
# ---------------------------------------------------------------------------

# Create mock modules for msal and O365 so import of src.auth succeeds
_mock_msal = MagicMock()
_mock_msal.SerializableTokenCache = MagicMock
_mock_msal.PublicClientApplication = MagicMock

_mock_O365 = MagicMock()
_mock_O365_utils = MagicMock()


@pytest.fixture(autouse=True)
def _patch_heavy_imports(monkeypatch, tmp_path):
    """Patch msal, O365, and cc_shared.config before every test.

    This ensures:
    - No real MSAL / O365 code runs
    - File-system paths point to tmp_path so tests are isolated
    """
    import sys

    # Patch third-party modules
    monkeypatch.setitem(sys.modules, "msal", _mock_msal)
    monkeypatch.setitem(sys.modules, "O365", _mock_O365)
    monkeypatch.setitem(sys.modules, "O365.utils", _mock_O365_utils)

    # Provide a BaseTokenBackend stand-in so the class definition works
    _mock_O365_utils.BaseTokenBackend = type("BaseTokenBackend", (), {
        "__init__": lambda self, *a, **kw: None,
    })

    # Patch cc_shared.config.get_data_dir to return tmp_path
    fake_data_dir = tmp_path / "cc-tools-data"
    fake_data_dir.mkdir()

    mock_config_module = MagicMock()
    mock_config_module.get_data_dir.return_value = fake_data_dir
    monkeypatch.setitem(sys.modules, "cc_shared", MagicMock())
    monkeypatch.setitem(sys.modules, "cc_shared.config", mock_config_module)

    # Force-reload src.auth so it picks up patched modules
    if "src.auth" in sys.modules:
        del sys.modules["src.auth"]

    # Import the module now that dependencies are mocked
    import src.auth as auth_module

    # Override module-level paths to point at tmp_path
    auth_module.CONFIG_DIR = fake_data_dir / "outlook"
    auth_module.PROFILES_FILE = auth_module.CONFIG_DIR / "profiles.json"
    auth_module.TOKENS_DIR = auth_module.CONFIG_DIR / "tokens"

    # Ensure directories exist
    auth_module.CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    auth_module.TOKENS_DIR.mkdir(parents=True, exist_ok=True)

    yield auth_module


# ===========================================================================
# Helpers
# ===========================================================================

def _write_profiles(auth_module, profiles_dict: dict) -> None:
    """Write a profiles.json file using the module's PROFILES_FILE path."""
    auth_module.PROFILES_FILE.write_text(
        json.dumps(profiles_dict), encoding="utf-8"
    )


# ===========================================================================
# get_msal_token_path
# ===========================================================================

class TestGetMsalTokenPath:

    def test_returns_correct_path_format(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        result = auth.get_msal_token_path("user@example.com")
        expected_name = "user_example_com_msal.json"
        assert result.name == expected_name
        assert result.parent == auth.TOKENS_DIR

    def test_at_and_dots_replaced(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        result = auth.get_msal_token_path("first.last@corp.example.org")
        assert "@" not in result.name
        # All dots in the email part become underscores
        assert result.name == "first_last_corp_example_org_msal.json"


# ===========================================================================
# MSALTokenBackend.token_is_expired / should_refresh_token
# ===========================================================================

class TestMSALTokenBackendOverrides:

    def _make_backend(self, auth_module, tmp_path):
        """Create an MSALTokenBackend instance with mocked internals."""
        token_path = tmp_path / "tokens" / "test_msal.json"
        token_path.parent.mkdir(parents=True, exist_ok=True)

        # Patch _load_cache so it does not attempt real file I/O via msal
        with patch.object(auth_module.MSALTokenBackend, "_load_cache"):
            backend = auth_module.MSALTokenBackend(
                client_id="fake-client-id",
                token_path=token_path,
            )
        return backend

    def test_token_is_expired_returns_false(self, _patch_heavy_imports, tmp_path):
        auth = _patch_heavy_imports
        backend = self._make_backend(auth, tmp_path)
        assert backend.token_is_expired() is False
        assert backend.token_is_expired(username="anyone") is False

    def test_should_refresh_token_returns_false(self, _patch_heavy_imports, tmp_path):
        auth = _patch_heavy_imports
        backend = self._make_backend(auth, tmp_path)
        assert backend.should_refresh_token() is False
        assert backend.should_refresh_token(con="anything") is False


# ===========================================================================
# resolve_account
# ===========================================================================

class TestResolveAccount:

    def test_explicit_account_found(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {
            "profiles": {
                "user@example.com": {"client_id": "cid123", "tenant_id": "common"}
            },
            "default": "user@example.com",
        }
        _write_profiles(auth, profiles)

        result = auth.resolve_account("user@example.com")
        assert result == "user@example.com"

    def test_explicit_account_not_found_raises(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {
            "profiles": {
                "real@example.com": {"client_id": "cid", "tenant_id": "common"}
            },
            "default": "real@example.com",
        }
        _write_profiles(auth, profiles)

        with pytest.raises(ValueError, match="not found"):
            auth.resolve_account("bogus@example.com")

    def test_none_with_default_account(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {
            "profiles": {
                "default@example.com": {"client_id": "cid", "tenant_id": "common"}
            },
            "default": "default@example.com",
        }
        _write_profiles(auth, profiles)

        result = auth.resolve_account(None)
        assert result == "default@example.com"

    def test_none_with_no_default_raises(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {
            "profiles": {
                "user@example.com": {"client_id": "cid", "tenant_id": "common"}
            },
            "default": None,
        }
        _write_profiles(auth, profiles)

        with pytest.raises(ValueError, match="No default account"):
            auth.resolve_account(None)

    def test_none_with_empty_profiles_raises(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {"profiles": {}, "default": None}
        _write_profiles(auth, profiles)

        with pytest.raises(ValueError, match="No default account"):
            auth.resolve_account(None)

    def test_no_argument_defaults_to_none(self, _patch_heavy_imports):
        auth = _patch_heavy_imports
        profiles = {
            "profiles": {
                "solo@example.com": {"client_id": "cid", "tenant_id": "common"}
            },
            "default": "solo@example.com",
        }
        _write_profiles(auth, profiles)

        # Call with no arguments (should behave like account_name=None)
        result = auth.resolve_account()
        assert result == "solo@example.com"
