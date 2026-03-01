"""Data models for cc-personresearch."""

from datetime import datetime
from typing import Any, Optional

from pydantic import BaseModel, Field


class SearchParams(BaseModel):
    """Input parameters for a person search."""
    name: str
    email: Optional[str] = None
    location: Optional[str] = None
    timestamp: str = Field(default_factory=lambda: datetime.utcnow().isoformat() + "Z")


class SourceResult(BaseModel):
    """Result from a single data source."""
    source: str
    status: str  # "found", "not_found", "error", "skipped"
    query_time_ms: int = 0
    error_message: Optional[str] = None
    data: dict[str, Any] = Field(default_factory=dict)


class SearchSummary(BaseModel):
    """Summary of the search results."""
    total_sources: int = 0
    sources_with_results: int = 0
    sources_failed: int = 0
    sources_skipped: int = 0
    note: str = (
        "Raw OSINT data. Multiple results may refer to different people "
        "with the same name. Manual review required."
    )


class PersonReport(BaseModel):
    """Complete person research report."""
    search_params: SearchParams
    summary: SearchSummary = Field(default_factory=SearchSummary)
    sources: dict[str, SourceResult] = Field(default_factory=dict)
    discovered_urls: list[str] = Field(default_factory=list)

    def add_result(self, result: SourceResult) -> None:
        """Add a source result to the report."""
        self.sources[result.source] = result
        self.summary.total_sources += 1
        if result.status == "found":
            self.summary.sources_with_results += 1
        elif result.status == "error":
            self.summary.sources_failed += 1
        elif result.status == "skipped":
            self.summary.sources_skipped += 1

    def add_url(self, url: str) -> None:
        """Add a discovered URL if not already present."""
        if url and url not in self.discovered_urls:
            self.discovered_urls.append(url)
