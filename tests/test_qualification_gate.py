"""Adversarial exact-commit workflow gating; synthetic API, no credentials."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "qualification_gate", Path(__file__).resolve().parents[1] / "scripts/qualification_gate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
SHA = "a" * 40


class Api:
    def __init__(self):
        self.runs, self.jobs = {}, {}
        for number, (filename, names) in enumerate(gate.REQUIRED.items(), 1):
            self.runs[filename] = {
                "id": number, "run_attempt": 2, "path": ".github/workflows/" + filename,
                "head_sha": SHA, "head_branch": "main", "event": "push",
                "repository": {"full_name": gate.REPOSITORY},
                "status": "completed", "conclusion": "success",
            }
            self.jobs[number] = [{"id": number * 100 + index, "run_id": number,
                                  "head_sha": SHA, "name": name,
                                  "status": "completed", "conclusion": "success",
                                  "steps": [{"number": step_number, "name": step_name,
                                             "status": "completed", "conclusion": "success"}
                                            for step_number, step_name in enumerate(
                                                sorted(gate.REQUIRED_STEPS[name]), 1)]}
                                 for index, name in enumerate(sorted(names), 1)]
        self.reread_change = {}

    def __call__(self, path):
        if path.startswith("/actions/workflows/"):
            name = path.split("/")[3]
            runs = [self.runs[name]] if name in self.runs else []
            return {"total_count": len(runs), "workflow_runs": copy.deepcopy(runs)}
        number = int(path.split("/")[3])
        if "/jobs?" in path:
            return {"total_count": len(self.jobs[number]), "jobs": copy.deepcopy(self.jobs[number])}
        return copy.deepcopy(next(r for r in self.runs.values() if r["id"] == number)) | self.reread_change


class QualificationTests(unittest.TestCase):
    def test_workflow_gate_has_all_required_event_triggers_and_no_path_skips(self):
        root = Path(__file__).resolve().parents[1]
        self.assertEqual(set(gate.REQUIRED_STEPS), set().union(*gate.REQUIRED.values()))
        for filename in gate.REQUIRED:
            text = (root / ".github/workflows" / filename).read_text()
            with self.subTest(workflow=filename):
                self.assertIn("  pull_request:", text)
                self.assertIn("  push:", text)
                self.assertIn("branches: [main]", text)
                self.assertNotIn("    paths:", text)
                for job in gate.REQUIRED[filename]:
                    for step in gate.REQUIRED_STEPS[job]:
                        self.assertIn("      - name: " + step, text)
        ci = (root / ".github/workflows/ci.yml").read_text()
        self.assertIn("name: Complete qualification", ci)
        self.assertIn("needs: [privacy, test, maven, dashboard, container]", ci)
        self.assertIn("if: ${{ always() }}", ci)
        self.assertIn("actions: read", ci)

    def test_verified_index_contains_only_public_minimum(self):
        api = Api()
        api.runs["s3.yml"]["actor"] = {"private-identity": "must-never-export"}
        result = gate.qualify(api, SHA, "push")
        self.assertEqual(8, len(result["workflows"]))
        self.assertEqual("packory.ci-qualification/2", result["schema"])
        self.assertTrue(all(item["validatedSteps"] for item in result["workflows"]))
        self.assertFalse(result["productionRecommended"])
        self.assertFalse(result["independentApprovalVerified"])
        self.assertNotIn("must-never-export", json.dumps(result))

    def test_missing_and_running_are_pending(self):
        for mode in ("missing", "running", "queued"):
            with self.subTest(mode=mode):
                api = Api()
                if mode == "missing":
                    del api.runs["s3.yml"]
                else:
                    api.runs["s3.yml"].update(status=mode, conclusion=None)
                with self.assertRaises(gate.Pending):
                    gate.qualify(api, SHA, "push")

    def test_unsuccessful_workflows_never_qualify(self):
        for result in ("failure", "cancelled", "skipped", "neutral", "timed_out", "action_required", None):
            with self.subTest(result=result):
                api = Api()
                api.runs["s3.yml"]["conclusion"] = result
                with self.assertRaises(gate.Blocked):
                    gate.qualify(api, SHA, "push")

    def test_wrong_identity_or_workflow_path_never_qualifies(self):
        for change in ({"head_sha": "b" * 40}, {"event": "workflow_dispatch"},
                       {"repository": {"full_name": "foreign/project"}},
                       {"path": ".github/workflows/impostor.yml"}, {"head_branch": "other"},
                       {"id": True}, {"run_attempt": True}):
            with self.subTest(change=change):
                api = Api()
                api.runs["s3.yml"].update(change)
                with self.assertRaises(gate.Blocked):
                    gate.qualify(api, SHA, "push")

    def test_skipped_failed_missing_and_wrong_commit_jobs_fail(self):
        for change in ({"conclusion": "skipped"}, {"conclusion": "failure"},
                       {"name": "unexpected-job"}, {"head_sha": "b" * 40},
                       {"id": True}, {"run_id": 999}, {"status": "in_progress"}):
            with self.subTest(change=change):
                api = Api()
                api.jobs[api.runs["s3.yml"]["id"]][0].update(change)
                with self.assertRaises(gate.Blocked):
                    gate.qualify(api, SHA, "push")

    def test_every_matrix_job_required(self):
        api = Api()
        api.jobs[api.runs["native-matrix.yml"]["id"]].pop()
        with self.assertRaises(gate.Blocked):
            gate.qualify(api, SHA, "push")

    def test_required_operation_steps_must_have_one_successful_execution(self):
        for mode in ("missing", "duplicate", "skipped", "failure", "running", "invalid-number"):
            with self.subTest(mode=mode):
                api = Api()
                step_list = api.jobs[api.runs["skillspector-smoke.yml"]["id"]][0]["steps"]
                if mode == "missing":
                    step_list.pop(0)
                elif mode == "duplicate":
                    step_list.append(copy.deepcopy(step_list[0]) | {"number": 99})
                elif mode == "skipped":
                    step_list[0]["conclusion"] = "skipped"
                elif mode == "failure":
                    step_list[0]["conclusion"] = "failure"
                elif mode == "running":
                    step_list[0]["status"] = "in_progress"
                else:
                    step_list[0]["number"] = True
                with self.assertRaises(gate.Blocked):
                    gate.qualify(api, SHA, "push")

    def test_rerun_during_read_invalidates_old_green(self):
        for change in ({"run_attempt": 3}, {"status": "queued"}, {"conclusion": "failure"}):
            api = Api()
            api.reread_change = change
            with self.assertRaises(gate.Pending):
                gate.qualify(api, SHA, "push")

    def test_new_failed_run_cannot_fall_back_to_old_success(self):
        api = Api()
        def read(path):
            result = api(path)
            if path.startswith("/actions/workflows/s3.yml/"):
                result["workflow_runs"].append(api.runs["s3.yml"] | {"id": 1000, "conclusion": "failure"})
                result["total_count"] = 2
            return result
        with self.assertRaises(gate.Blocked):
            gate.qualify(read, SHA, "push")

    def test_incomplete_inventory_rejected(self):
        api = Api()
        def read(path):
            result = api(path)
            if "total_count" in result:
                result["total_count"] = 101
            return result
        with self.assertRaises(gate.Blocked):
            gate.qualify(read, SHA, "push")

    def test_duplicate_job_ids_rejected(self):
        api = Api()
        jobs = api.jobs[api.runs["native-matrix.yml"]["id"]]
        jobs[1]["id"] = jobs[0]["id"]
        with self.assertRaises(gate.Blocked):
            gate.qualify(api, SHA, "push")

    def test_pr_event_is_distinct_from_main_push(self):
        api = Api()
        for run in api.runs.values():
            run.update(event="pull_request", head_branch="feature")
        self.assertEqual("pull_request", gate.qualify(api, SHA, "pull_request")["event"])
        with self.assertRaises(gate.Pending):
            gate.qualify(api, SHA, "push")

    def test_no_stale_or_partial_index_on_failure_and_no_secret_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "evidence.json"
            with patch.dict("os.environ", {"GH_TOKEN": "synthetic-credential"}), \
                 patch.object(gate, "api_reader", side_effect=RuntimeError("private-data")), \
                 patch("sys.stderr") as stderr:
                self.assertEqual(1, gate.main(["--sha", SHA, "--event", "push", "--output", str(output)]))
                self.assertFalse(output.exists())
                self.assertNotIn("private-data", str(stderr.write.call_args_list))
            output.write_text("prior success")
            with patch.dict("os.environ", {"GH_TOKEN": "synthetic-credential"}), patch.object(gate, "api_reader") as reader:
                self.assertEqual(1, gate.main(["--sha", SHA, "--event", "push", "--output", str(output)]))
                reader.assert_not_called()


if __name__ == "__main__":
    unittest.main()
