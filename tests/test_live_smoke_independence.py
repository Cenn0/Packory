"""Static regressions for independent public NuGet/npm live-mirror acceptance."""
from pathlib import Path
import unittest

WORKFLOW = (Path(__file__).resolve().parents[1] / ".github/workflows/smoke-live.yml")


def _step(workflow: str, name: str) -> str:
    marker = f"      - name: {name}\n"
    if marker not in workflow:
        raise AssertionError(f"Missing live-mirror step: {name}")
    return workflow.split(marker, 1)[1].split("\n      - name: ", 1)[0]


class LiveSmokeIndependenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = WORKFLOW.read_text(encoding="utf-8")

    def test_startup_is_named_and_requires_readiness(self) -> None:
        start = _step(self.workflow, "Publish and serve Packory")
        self.assertIn("id: packory_start", start)
        self.assertIn("/health/ready", start)
        self.assertIn("exit 1", start)

    def test_public_readiness_suppresses_untrusted_output_and_is_bounded(self) -> None:
        start = _step(self.workflow, "Publish and serve Packory")
        # A failed application startup must not dump raw logs into public CI.
        self.assertNotIn("cat /tmp/packory.log", start)
        self.assertNotIn("curl -fsS", start)
        self.assertIn("--fail --silent --output /dev/null", start)
        self.assertIn("--connect-timeout 2 --max-time 3", start)
        self.assertIn("--proto '=http' --max-redirs 0", start)
        self.assertIn("server logs withheld from public CI", start)
        self.assertIn("exit 1", start)

    def test_npm_runs_after_nuget_failure_only_if_packory_started(self) -> None:
        nuget_name = "Restore a real package without fallback source"
        npm_name = "Install a real npm package through Packory"
        self.assertLess(self.workflow.index(nuget_name), self.workflow.index(npm_name))
        npm = _step(self.workflow, npm_name)
        self.assertIn("if: ${{ !cancelled() && steps.packory_start.outcome == 'success' }}", npm)
        self.assertIn("bash scripts/smoke-npm.sh", npm)

    def test_nuget_failure_remains_a_failed_job(self) -> None:
        nuget = _step(self.workflow, "Restore a real package without fallback source")
        self.assertIn("bash scripts/smoke-nuget.sh", nuget)
        self.assertNotIn("continue-on-error:", nuget)
        self.assertIn("if: always()", _step(self.workflow, "Stop service"))


if __name__ == "__main__":
    unittest.main()
