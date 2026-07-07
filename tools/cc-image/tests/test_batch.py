"""Tests for batch / folder cataloging."""

import csv
import json
import pytest
from pathlib import Path
from unittest.mock import MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.batch import (
    find_images,
    catalog_folder,
    describe_one,
    load_processed_paths,
    ImageRecord,
)
from src.devthrottle import DevThrottleTransientError


def _make_image(path: Path, color="red"):
    from PIL import Image

    path.parent.mkdir(parents=True, exist_ok=True)
    Image.new("RGB", (32, 32), color).save(path)
    return path


class TestFindImages:
    def test_non_recursive_top_level_only(self, tmp_path):
        _make_image(tmp_path / "a.png")
        _make_image(tmp_path / "b.jpg")
        _make_image(tmp_path / "sub" / "c.png")
        (tmp_path / "notes.txt").write_text("x")

        found = find_images(tmp_path, recursive=False)
        names = {p.name for p in found}
        assert names == {"a.png", "b.jpg"}

    def test_recursive_includes_subdirs(self, tmp_path):
        _make_image(tmp_path / "a.png")
        _make_image(tmp_path / "sub" / "c.png")
        found = find_images(tmp_path, recursive=True)
        names = {p.name for p in found}
        assert names == {"a.png", "c.png"}

    def test_ignores_non_images(self, tmp_path):
        _make_image(tmp_path / "a.png")
        (tmp_path / "b.txt").write_text("x")
        (tmp_path / "c.pdf").write_text("x")
        found = find_images(tmp_path, recursive=True)
        assert {p.name for p in found} == {"a.png"}

    def test_not_a_folder_raises(self, tmp_path):
        img = _make_image(tmp_path / "a.png")
        with pytest.raises(NotADirectoryError):
            find_images(img, recursive=False)


class TestDescribeOne:
    def test_success(self, tmp_path):
        img = _make_image(tmp_path / "a.png")
        client = MagicMock()
        client.describe_image.return_value = "a red square"
        record = describe_one(client, img)
        assert record.description == "a red square"
        assert record.error == ""

    def test_hard_error_captured(self, tmp_path):
        img = _make_image(tmp_path / "a.png")
        client = MagicMock()
        client.describe_image.side_effect = RuntimeError("401 bad key")
        record = describe_one(client, img)
        assert "401 bad key" in record.error
        assert record.description == ""

    def test_transient_retries_then_gives_up(self, tmp_path, monkeypatch):
        import src.batch as batch
        monkeypatch.setattr(batch.time, "sleep", lambda *_: None)

        img = _make_image(tmp_path / "a.png")
        client = MagicMock()
        client.describe_image.side_effect = DevThrottleTransientError("429")
        record = describe_one(client, img, max_retries=2)
        assert "transient" in record.error
        assert client.describe_image.call_count == 3  # initial + 2 retries

    def test_transient_then_success(self, tmp_path, monkeypatch):
        import src.batch as batch
        monkeypatch.setattr(batch.time, "sleep", lambda *_: None)

        img = _make_image(tmp_path / "a.png")
        client = MagicMock()
        client.describe_image.side_effect = [DevThrottleTransientError("429"), "ok now"]
        record = describe_one(client, img, max_retries=3)
        assert record.description == "ok now"


class TestCatalogFolder:
    def test_writes_json_and_csv(self, tmp_path):
        _make_image(tmp_path / "a.png")
        _make_image(tmp_path / "sub" / "b.jpg")

        client = MagicMock()
        client.describe_image.return_value = "desc"

        out = tmp_path / "out" / "catalog"
        result = catalog_folder(tmp_path, output_path=out, recursive=True, workers=2, client=client)

        assert result.total == 2
        assert result.processed == 2
        assert result.failed == 0

        json_path = out.with_suffix(".json")
        csv_path = out.with_suffix(".csv")
        assert json_path.exists() and csv_path.exists()

        data = json.loads(json_path.read_text(encoding="utf-8"))
        assert len(data) == 2
        assert all(r["description"] == "desc" for r in data)

        rows = list(csv.DictReader(csv_path.open(encoding="utf-8")))
        assert len(rows) == 2
        assert rows[0]["path"] and rows[0]["description"] == "desc"

    def test_resume_skips_already_processed(self, tmp_path):
        _make_image(tmp_path / "a.png")
        _make_image(tmp_path / "b.png")
        out = tmp_path / "catalog"

        client = MagicMock()
        client.describe_image.return_value = "first pass"
        catalog_folder(tmp_path, output_path=out, recursive=True, client=client)
        assert client.describe_image.call_count == 2

        # Second run: everything is already in the JSON, so nothing is re-described.
        client2 = MagicMock()
        client2.describe_image.return_value = "second pass"
        result = catalog_folder(tmp_path, output_path=out, recursive=True, client=client2)
        assert client2.describe_image.call_count == 0
        assert result.skipped == 2
        assert result.processed == 0

    def test_resume_retries_failed(self, tmp_path):
        _make_image(tmp_path / "a.png")
        out = tmp_path / "catalog"

        # First run fails the image.
        client = MagicMock()
        client.describe_image.side_effect = RuntimeError("401 bad key")
        catalog_folder(tmp_path, output_path=out, recursive=True, client=client)

        data = json.loads(out.with_suffix(".json").read_text(encoding="utf-8"))
        assert data[0].get("error")

        # Second run should retry the failed image (errors are not "done").
        client2 = MagicMock()
        client2.describe_image.return_value = "recovered"
        result = catalog_folder(tmp_path, output_path=out, recursive=True, client=client2)
        assert client2.describe_image.call_count == 1
        assert result.processed == 1

    def test_progress_callback_invoked(self, tmp_path):
        _make_image(tmp_path / "a.png")
        _make_image(tmp_path / "b.png")
        out = tmp_path / "catalog"

        client = MagicMock()
        client.describe_image.return_value = "d"

        seen = []
        catalog_folder(
            tmp_path, output_path=out, recursive=True, client=client,
            on_progress=lambda done, total, rec: seen.append((done, total)),
        )
        assert len(seen) == 2
        assert seen[-1] == (2, 2)


class TestLoadProcessed:
    def test_missing_file(self, tmp_path):
        assert load_processed_paths(tmp_path / "none.json") == {}

    def test_excludes_errored_records(self, tmp_path):
        p = tmp_path / "c.json"
        p.write_text(json.dumps([
            {"path": "/x/a.png", "description": "ok"},
            {"path": "/x/b.png", "description": "", "error": "boom"},
        ]), encoding="utf-8")
        processed = load_processed_paths(p)
        assert "/x/a.png" in processed
        assert "/x/b.png" not in processed
