"""Tests for the cc-devthrottle workflow command group (Workflows mission, phase 3).

The Gateway is mocked at the requests layer (the endpoint behavior itself is proven by the
Gateway's own test suite); what these tests pin is the CLI's half of the contract: the
directory round-trip (pull writes exactly what push reads), the create-versus-draft decision,
the If-Match sidecar discipline, and the raw, unmangled instructions output an agent's
context depends on.
"""

import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import requests
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import workflow_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _fake_response(status_code: int, json_body=None, text: str = "") -> MagicMock:
    resp = MagicMock(spec=requests.Response)
    resp.status_code = status_code
    resp.content = b"x" if (json_body is not None or text) else b""
    resp.text = text
    if json_body is not None:
        resp.json.return_value = json_body
    else:
        resp.json.side_effect = ValueError("no json")
    return resp


DETAIL = {
    "workflowId": "release-train",
    "version": 2,
    "status": "draft",
    "name": "Release train",
    "summary": "Cut and announce.",
    "whenToUse": "When a release is due.",
    "humanCheckpoint": "Once.",
    "steps": [{"name": "Cut", "description": "d", "doer": "Worker", "reviewer": None, "done": "Tag pushed."}],
    "instructionsMarkdown": "# Release train\n\nDo it.",
    "outcomeCriteria": [{"criterionId": "announced", "description": "Notes went out.", "proofHint": None}],
    "files": [{"fileName": "verify.py", "contentHash": "abc", "content": "print('verify')"}],
    "contentHash": "bundle-hash-2",
}


class TestActionsDiscoverability:
    def test_actions_json_lists_the_workflow_commands(self):
        result = runner.invoke(app, ["actions", "--json"])
        assert result.exit_code == 0
        ids = {action["id"] for action in json.loads(result.output)["actions"]}
        assert {
            "workflow-list",
            "workflow-instructions",
            "workflow-show",
            "workflow-versions",
            "workflow-pull",
            "workflow-push",
            "workflow-publish",
            "workflow-materialize",
            "workflow-reset",
            "workflow-delete",
            "workflow-runs",
            "workflow-run-show",
        } <= ids


class TestInstructionsOutput:
    def test_instructions_prints_the_raw_markdown_unmangled(self, capsys):
        client = MagicMock()
        client.get_instructions.return_value = "# Conduct\n\n*follow me* [exactly](x)\n"
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.print_instructions("mission", None)
        out = capsys.readouterr().out
        # Byte-identical: this output lands in an agent's context, so no rich rendering,
        # no wrapping, no markup interpretation.
        assert out == "# Conduct\n\n*follow me* [exactly](x)\n"


class TestPullPushRoundTrip:
    def test_pull_writes_the_directory_and_the_hash_sidecar(self, tmp_path):
        client = MagicMock()
        client.list_versions.return_value = [
            {"version": 2, "status": "draft"},
            {"version": 1, "status": "published"},
        ]
        client.get_version_detail.return_value = DETAIL
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.pull_workflow("release-train", str(tmp_path), None)

        # The draft wins as the authoring baseline.
        client.get_version_detail.assert_called_once_with("release-train", 2)
        metadata = json.loads((tmp_path / "workflow.json").read_text(encoding="utf-8"))
        assert metadata["id"] == "release-train"
        assert metadata["steps"][0]["doer"] == "Worker"
        assert (tmp_path / "instructions.md").read_text(encoding="utf-8") == "# Release train\n\nDo it."
        assert (tmp_path / "helpers" / "verify.py").read_text(encoding="utf-8") == "print('verify')"
        assert (tmp_path / ".workflow-hash").read_text(encoding="utf-8") == "bundle-hash-2"

    def test_push_reads_back_exactly_what_pull_wrote(self, tmp_path):
        pull_client = MagicMock()
        pull_client.list_versions.return_value = [{"version": 2, "status": "draft"}]
        pull_client.get_version_detail.return_value = DETAIL
        with patch.object(workflow_ops, "_client", return_value=pull_client):
            workflow_ops.pull_workflow("release-train", str(tmp_path), None)

        push_client = MagicMock()
        push_client.workflow_exists.return_value = True
        push_client.update_draft.return_value = {"version": 2, "contentHash": "bundle-hash-3"}
        with patch.object(workflow_ops, "_client", return_value=push_client):
            workflow_ops.push_workflow("release-train", str(tmp_path), "a note")

        (_, body, if_match), _kwargs = (
            push_client.update_draft.call_args.args,
            push_client.update_draft.call_args.kwargs,
        )
        assert body["name"] == "Release train"
        assert body["instructionsMarkdown"] == "# Release train\n\nDo it."
        assert body["files"] == [{"fileName": "verify.py", "content": "print('verify')"}]
        assert body["changeNote"] == "a note"
        # The sidecar hash from pull rode along as If-Match, and the new hash replaced it.
        assert if_match == "bundle-hash-2"
        assert (tmp_path / ".workflow-hash").read_text(encoding="utf-8") == "bundle-hash-3"

    def test_push_creates_the_workflow_when_it_does_not_exist(self, tmp_path):
        (tmp_path / "workflow.json").write_text(
            json.dumps({"id": "new-flow", "name": "New flow", "summary": "s"}), encoding="utf-8"
        )
        (tmp_path / "instructions.md").write_text("# New flow", encoding="utf-8")

        client = MagicMock()
        client.workflow_exists.return_value = False
        client.create.return_value = {"version": 1, "contentHash": "h1"}
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.push_workflow("new-flow", str(tmp_path), None)

        client.create.assert_called_once()
        client.update_draft.assert_not_called()
        body = client.create.call_args.args[0]
        assert body["id"] == "new-flow"
        assert body["authoredBy"]  # authorship always travels

    def test_push_refuses_a_directory_declaring_a_different_id(self, tmp_path):
        (tmp_path / "workflow.json").write_text(
            json.dumps({"id": "other-flow", "name": "n", "summary": "s"}), encoding="utf-8"
        )
        client = MagicMock()
        with patch.object(workflow_ops, "_client", return_value=client):
            result = runner.invoke(
                app, ["workflow", "push", "new-flow", "--dir", str(tmp_path)]
            )
        assert result.exit_code == 1
        client.create.assert_not_called()
        client.update_draft.assert_not_called()


class TestInspectionHardening:
    def test_pull_refuses_an_unsafe_server_supplied_file_name(self, tmp_path):
        detail = dict(DETAIL)
        detail["files"] = [{"fileName": "..\\escape.py", "contentHash": "x", "content": "boom"}]
        client = MagicMock()
        client.list_versions.return_value = [{"version": 2, "status": "draft"}]
        client.get_version_detail.return_value = detail
        with patch.object(workflow_ops, "_client", return_value=client):
            result = runner.invoke(
                app, ["workflow", "pull", "release-train", "--dir", str(tmp_path)]
            )
        assert result.exit_code == 1
        assert not (tmp_path.parent / "escape.py").exists()

    def test_repull_removes_helpers_the_server_no_longer_has(self, tmp_path):
        client = MagicMock()
        client.list_versions.return_value = [{"version": 2, "status": "draft"}]
        client.get_version_detail.return_value = DETAIL
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.pull_workflow("release-train", str(tmp_path), None)
        # Another author deletes the helper on the Gateway; the next pull must not leave the
        # local copy behind to be resurrected by the next push.
        no_files = dict(DETAIL)
        no_files["files"] = []
        client.get_version_detail.return_value = no_files
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.pull_workflow("release-train", str(tmp_path), None)
        assert not (tmp_path / "helpers" / "verify.py").exists()

    def test_push_update_without_sidecar_is_refused_unless_forced(self, tmp_path):
        (tmp_path / "workflow.json").write_text(
            json.dumps({"id": "release-train", "name": "n", "summary": "s"}), encoding="utf-8"
        )
        client = MagicMock()
        client.workflow_exists.return_value = True
        client.update_draft.return_value = {"version": 3, "contentHash": "h"}
        with patch.object(workflow_ops, "_client", return_value=client):
            refused = runner.invoke(
                app, ["workflow", "push", "release-train", "--dir", str(tmp_path)]
            )
            assert refused.exit_code == 1
            client.update_draft.assert_not_called()

            forced = runner.invoke(
                app, ["workflow", "push", "release-train", "--dir", str(tmp_path), "--force"]
            )
            assert forced.exit_code == 0
            client.update_draft.assert_called_once()

    def test_wrong_shaped_workflow_json_fails_cleanly(self, tmp_path):
        (tmp_path / "workflow.json").write_text("[]", encoding="utf-8")
        client = MagicMock()
        with patch.object(workflow_ops, "_client", return_value=client):
            result = runner.invoke(
                app, ["workflow", "push", "x-flow", "--dir", str(tmp_path)]
            )
        assert result.exit_code == 1
        client.create.assert_not_called()

    def test_round_trip_preserves_carriage_returns_exactly(self, tmp_path):
        detail = dict(DETAIL)
        detail["instructionsMarkdown"] = "line one\r\nline two\n"
        client = MagicMock()
        client.list_versions.return_value = [{"version": 2, "status": "draft"}]
        client.get_version_detail.return_value = detail
        with patch.object(workflow_ops, "_client", return_value=client):
            workflow_ops.pull_workflow("release-train", str(tmp_path), None)

        push_client = MagicMock()
        push_client.workflow_exists.return_value = True
        push_client.update_draft.return_value = {"version": 2, "contentHash": "h"}
        with patch.object(workflow_ops, "_client", return_value=push_client):
            workflow_ops.push_workflow("release-train", str(tmp_path), None)
        body = push_client.update_draft.call_args.args[1]
        assert body["instructionsMarkdown"] == "line one\r\nline two\n"


class TestAuthoringVersionPick:
    def test_draft_wins_over_published(self):
        rows = [{"version": 3, "status": "published"}, {"version": 4, "status": "draft"}]
        assert workflow_ops._pick_authoring_version(rows)["version"] == 4

    def test_published_when_no_draft(self):
        rows = [{"version": 3, "status": "published"}, {"version": 2, "status": "superseded"}]
        assert workflow_ops._pick_authoring_version(rows)["version"] == 3

    def test_none_when_empty(self):
        assert workflow_ops._pick_authoring_version([]) is None
