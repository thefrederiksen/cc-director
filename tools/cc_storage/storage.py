"""Centralized storage path resolution for cc-director.

All storage paths are resolved through this module. Tools and apps call
CcStorage methods instead of computing paths themselves.

Storage categories:
    vault   - Personal data: contacts, docs, tasks, goals, health, vectors
    config  - Tool settings, OAuth tokens, credentials, app state
    output  - Generated files: PDFs, reports, transcripts, exports
    logs    - All application and tool logs
    bin     - Installed executables (tool binaries)

Environment variable overrides:
    CC_DIRECTOR_ROOT - Override the base directory (default: %LOCALAPPDATA%/cc-director)
    CC_VAULT_PATH    - Override the vault directory specifically
"""

import os
from pathlib import Path


class CcStorage:
    """Single source of truth for all cc-director storage paths."""

    # -- Root categories --

    @staticmethod
    def _base() -> Path:
        """Base directory for cc-director local app data."""
        override = os.environ.get("CC_DIRECTOR_ROOT")
        if override:
            return Path(override)
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / "cc-director"
        return Path.home() / ".cc-director"

    @staticmethod
    def vault() -> Path:
        """Personal data: vault.db, vectors, documents, health, media."""
        override = os.environ.get("CC_VAULT_PATH")
        if override:
            return Path(override)
        return CcStorage._base() / "vault"

    @staticmethod
    def config() -> Path:
        """Tool settings, OAuth tokens, credentials, app state."""
        return CcStorage._base() / "config"

    @staticmethod
    def output() -> Path:
        """Generated files: PDFs, reports, transcripts, exports."""
        docs = os.environ.get("USERPROFILE")
        if docs:
            return Path(docs) / "Documents" / "cc-director"
        return Path.home() / "Documents" / "cc-director"

    @staticmethod
    def logs() -> Path:
        """All application and tool logs."""
        return CcStorage._base() / "logs"

    @staticmethod
    def bin() -> Path:
        """Installed executables (tool binaries)."""
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / "cc-director" / "bin"
        return Path.home() / ".cc-director" / "bin"

    # -- Tool-specific shortcuts --

    @staticmethod
    def tool_config(tool: str) -> Path:
        """Config directory for a specific tool: config/{tool}/"""
        return CcStorage.config() / tool

    @staticmethod
    def tool_output(tool: str) -> Path:
        """Output directory for a specific tool: output/{tool}/"""
        return CcStorage.output() / tool

    @staticmethod
    def tool_logs(tool: str) -> Path:
        """Log directory for a specific tool: logs/{tool}/"""
        return CcStorage.logs() / tool

    # -- Vault subdirectories --

    @staticmethod
    def vault_db() -> Path:
        """Main personal data database: vault/vault.db"""
        return CcStorage.vault() / "vault.db"

    @staticmethod
    def engine_db() -> Path:
        """Job scheduler state database: vault/engine.db"""
        return CcStorage.vault() / "engine.db"

    @staticmethod
    def vault_documents() -> Path:
        """Imported files: vault/documents/"""
        return CcStorage.vault() / "documents"

    @staticmethod
    def vault_vectors() -> Path:
        """Embeddings: vault/vectors/"""
        return CcStorage.vault() / "vectors"

    @staticmethod
    def vault_media() -> Path:
        """Media files: vault/media/"""
        return CcStorage.vault() / "media"

    @staticmethod
    def vault_health() -> Path:
        """Health data: vault/health/"""
        return CcStorage.vault() / "health"

    @staticmethod
    def vault_backups() -> Path:
        """Backup files: vault/backups/"""
        return CcStorage.vault() / "backups"

    @staticmethod
    def vault_imports() -> Path:
        """Staging for ingest: vault/imports/"""
        return CcStorage.vault() / "imports"

    # -- Config shortcuts --

    @staticmethod
    def config_json() -> Path:
        """Shared settings file: config/config.json"""
        return CcStorage.config() / "config.json"

    @staticmethod
    def comm_queue_db() -> Path:
        """Communication queue database: config/comm-queue/communications.db"""
        return CcStorage.tool_config("comm-queue") / "communications.db"

    # -- Output shortcuts --

    @staticmethod
    def output_reports() -> Path:
        """Generated PDFs and DOCX: output/reports/"""
        return CcStorage.output() / "reports"

    @staticmethod
    def output_transcripts() -> Path:
        """Whisper/transcribe output: output/transcripts/"""
        return CcStorage.output() / "transcripts"

    @staticmethod
    def output_screenshots() -> Path:
        """cc-trisight captures: output/screenshots/"""
        return CcStorage.output() / "screenshots"

    @staticmethod
    def output_diagrams() -> Path:
        """cc-docgen C4 diagrams: output/diagrams/"""
        return CcStorage.output() / "diagrams"

    # -- Utilities --

    @staticmethod
    def ensure(path: Path) -> Path:
        """Create directory if it doesn't exist and return the path."""
        path.mkdir(parents=True, exist_ok=True)
        return path
