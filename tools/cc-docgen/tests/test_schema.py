"""Tests for cc-docgen schema validation."""

import pytest
import yaml
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.schema import load_manifest, validate_manifest


FIXTURES_DIR = Path(__file__).parent.parent / "test" / "fixtures" / "input"


class TestLoadManifest:
    """Tests for YAML manifest loading."""

    def test_load_valid_manifest(self):
        """Valid manifest loads without errors."""
        manifest_path = FIXTURES_DIR / "valid_manifest.yaml"
        if manifest_path.exists():
            manifest = load_manifest(manifest_path)
            assert manifest is not None
            assert "project" in manifest
            assert "context" in manifest

    def test_load_missing_file_raises(self):
        """Missing manifest file raises appropriate error."""
        with pytest.raises((FileNotFoundError, SystemExit)):
            load_manifest(Path("/nonexistent/manifest.yaml"))

    def test_load_invalid_yaml_raises(self):
        """Invalid YAML syntax raises error."""
        invalid_path = FIXTURES_DIR / "invalid_yaml.yaml"
        if invalid_path.exists():
            with pytest.raises((yaml.YAMLError, SystemExit)):
                load_manifest(invalid_path)


class TestValidateManifest:
    """Tests for manifest schema validation."""

    def test_valid_manifest_passes(self):
        """Complete manifest passes validation."""
        manifest = {
            "schema_version": "1.0.0",
            "project": {"name": "Test", "description": "Test project"},
            "context": {
                "system": {
                    "name": "TestSystem",
                    "description": "A test system",
                    "technology": "Python",
                }
            },
        }
        # Should not raise
        result = validate_manifest(manifest)
        assert result is True or result is None  # Depends on implementation

    def test_missing_project_name_fails(self):
        """Manifest without project.name fails validation."""
        manifest = {
            "project": {"description": "No name"},
            "context": {
                "system": {
                    "name": "Sys",
                    "description": "Desc",
                    "technology": "Tech",
                }
            },
        }
        with pytest.raises((ValueError, KeyError, SystemExit)):
            validate_manifest(manifest)

    def test_missing_system_fails(self):
        """Manifest without context.system fails validation."""
        manifest = {
            "project": {"name": "Test", "description": "Test"},
            "context": {},
        }
        with pytest.raises((ValueError, KeyError, SystemExit)):
            validate_manifest(manifest)
