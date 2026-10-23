"""Regression guard for main GHCR image qualification and obsolete CI-run skips.

This inspects the declared workflow, not a substitute for a real GHCR pull.
"""
from pathlib import Path
import unittest

WORKFLOW = (Path(__file__).resolve().parents[1] /
            ".github/workflows/container-publish.yml").read_text(encoding="utf-8")


def step_block(title):
    heading = "      - name: " + title + "\n"
    if WORKFLOW.count(heading) != 1:
        raise AssertionError("Expected exactly one workflow step: " + title)
    return WORKFLOW.split(heading, 1)[1].split("\n      - name: ", 1)[0]


class ImagePublicationContractTests(unittest.TestCase):
    def test_only_successful_main_push_from_this_repository(self):
        for phrase in (
            "github.event.workflow_run.event == 'push'",
            "github.event.workflow_run.conclusion == 'success'",
            "github.event.workflow_run.head_branch == 'main'",
            "github.event.workflow_run.head_repository.full_name == github.repository",
        ):
            self.assertIn(phrase, WORKFLOW)

    def test_superseded_commit_skipped_before_new_build_files_are_read(self):
        section = step_block(
            "Verify CI source and skip superseded commits before using new build files")
        self.assertIn("id: eligibility", section)
        self.assertIn('test "$(git rev-parse HEAD)" = "$SOURCE_SHA"', section)
        self.assertIn('git ls-remote origin refs/heads/main', section)
        self.assertLess(section.index('echo "eligible=false"'),
                        section.index("docker compose -f compose.ghcr.yaml"))
        self.assertIn('echo "eligible=true"', section)
        for title in (
            "Build Packory and offline SkillSpector images",
            "Smoke-test images before publishing",
            "Publish SHA and main tags to GitHub Container Registry",
        ):
            self.assertIn("if: steps.eligibility.outputs.eligible == 'true'",
                          step_block(title))

    def test_exact_revision_and_digest_are_verified_before_moving_main(self):
        section = step_block("Publish SHA and main tags to GitHub Container Registry")
        self.assertIn('docker pull "$image:sha-$SOURCE_SHA"', section)
        self.assertIn('test "$actual_revision" = "$SOURCE_SHA"', section)
        self.assertIn("image_digest=", section)
        self.assertLess(section.index("Registry-authenticated SHA image read-back"),
                        section.index('docker tag "$image:build" "$image:main"'))

    def test_github_summary_never_executes_markdown_as_shell_commands(self):
        section = step_block("Publish SHA and main tags to GitHub Container Registry")
        for line in section.splitlines():
            if '>> "$GITHUB_STEP_SUMMARY"' in line:
                with self.subTest(line=line.strip()):
                    # An unescaped Markdown backtick inside a Bash double-quoted
                    # echo is command substitution, not formatting.
                    self.assertNotRegex(line, r"(?<!\\)`")

    def test_full_anonymous_delivery_is_required_before_moving_mutable_tags(self):
        section = step_block("Publish SHA and main tags to GitHub Container Registry")
        self.assertIn('env -u GHCR_TOKEN python3 scripts/verify_container_delivery.py', section)
        self.assertLess(section.index('scripts/verify_container_delivery.py'),
                        section.index('docker tag "$image:build" "$image:main"'))
        self.assertIn("NOT independently approved production releases", section)
        self.assertIn("container-delivery-${{ env.SOURCE_SHA }}", WORKFLOW)


if __name__ == "__main__":
    unittest.main()
