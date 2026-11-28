"""Static preflight for the application and isolated database-worker images.

The SDK must support both projects; each ASP.NET runtime must supply its
framework-dependent target under the default Minor roll-forward policy.
This is not a container execution or vulnerability scan.
"""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DOCKERFILE = ROOT / "Dockerfile"
CSPROJ = ROOT / "src" / "Packory" / "Packory.csproj"
WORKER = ROOT / "workers" / "database-connector" / "Packory.DatabaseConnectorWorker.csproj"

# Dynamic or unfamiliar stage layouts require explicit review.
IMAGE = re.compile(
    r"^FROM\s+mcr\.microsoft\.com/dotnet/(sdk|aspnet):"
    r"([1-9][0-9]*)\.([0-9]+)(?:\.[0-9]+)?(?:-[a-zA-Z0-9._-]+)?"
    r"(?:@sha256:[a-f0-9]{64})?\s+AS\s+([a-zA-Z0-9._-]+)\s*$",
    re.IGNORECASE,
)
FRAMEWORK = re.compile(r"net([1-9][0-9]*)\.([0-9]+)")


def target_version(project: str) -> tuple[int, int]:
    try:
        root = ET.fromstring(project)
    except ET.ParseError as error:
        raise ValueError("Cannot parse target framework") from error
    frameworks = [e.text.strip() for e in root.findall(".//TargetFramework") if e.text]
    if len(frameworks) != 1 or not FRAMEWORK.fullmatch(frameworks[0]):
        raise ValueError("Project must declare exactly one concrete .NET target framework")
    match = FRAMEWORK.fullmatch(frameworks[0])
    return int(match.group(1)), int(match.group(2))


def validate_alignment(dockerfile: str, project: str, worker_project: str) -> None:
    targets = {"packory": target_version(project), "database-worker": target_version(worker_project)}
    stages = []
    for line in dockerfile.splitlines():
        if not re.match(r"^\s*FROM\s+", line, re.IGNORECASE):
            continue
        match = IMAGE.fullmatch(line.strip())
        if not match:
            raise ValueError("Unsupported or dynamic .NET base image or stage")
        flavor, major, minor, name = match.groups()
        stages.append((name.lower(), flavor.lower(), (int(major), int(minor))))
    if [(name, flavor) for name, flavor, _ in stages] != [
        ("build", "sdk"), ("database-worker", "aspnet"), ("packory", "aspnet")
    ]:
        raise ValueError("Expected SDK build, database-worker runtime and Packory runtime as final stage")
    sdk = stages[0][2]
    for name, _, runtime in stages[1:]:
        target = targets[name]
        if runtime[0] != target[0]:
            raise ValueError(f"{name}: runtime major differs from target framework")
        if runtime < target:
            raise ValueError(f"{name}: runtime minor is older than target framework")
        if sdk < target:
            raise ValueError(f"{name}: SDK version is older than target framework")


class ContainerRuntimeAlignmentTests(unittest.TestCase):
    PROJECT = '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    BASE = ('FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n'
            'FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS database-worker\n'
            'FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS packory\n')

    def test_repository_dockerfile_matches_both_projects(self):
        validate_alignment(DOCKERFILE.read_text(encoding="utf-8"), CSPROJ.read_text(encoding="utf-8"),
                           WORKER.read_text(encoding="utf-8"))

    def test_runtime_major_bump_alone_is_rejected_for_each_stage(self):
        for name in ("database-worker", "packory"):
            with self.subTest(stage=name), self.assertRaisesRegex(ValueError, f"{name}: runtime major"):
                validate_alignment(self.BASE.replace(f"aspnet:10.0 AS {name}", f"aspnet:11.0 AS {name}"),
                                   self.PROJECT, self.PROJECT)

    def test_newer_sdk_alone_does_not_imply_newer_runtime(self):
        validate_alignment(self.BASE.replace("sdk:10.0", "sdk:11.0"), self.PROJECT, self.PROJECT)

    def test_old_runtime_minor_and_sdk_are_rejected(self):
        target = self.PROJECT.replace("net10.0", "net10.1")
        with self.assertRaisesRegex(ValueError, "database-worker: runtime minor"):
            validate_alignment(self.BASE, self.PROJECT, target)
        with self.assertRaisesRegex(ValueError, "packory: runtime minor"):
            validate_alignment(self.BASE, target, self.PROJECT)
        with self.assertRaisesRegex(ValueError, "database-worker: SDK version"):
            validate_alignment(self.BASE.replace("aspnet:10.0 AS database-worker", "aspnet:10.1 AS database-worker"),
                               self.PROJECT, target)
        with self.assertRaisesRegex(ValueError, "SDK version"):
            validate_alignment(self.BASE.replace("sdk:10.0", "sdk:9.0"), self.PROJECT, self.PROJECT)

    def test_newer_minor_runtime_can_satisfy_target(self):
        validate_alignment(self.BASE.replace("aspnet:10.0", "aspnet:10.1"), self.PROJECT, self.PROJECT)

    def test_final_stage_must_be_packory(self):
        lines = self.BASE.splitlines()
        for order in ([lines[0], lines[2], lines[1]], [lines[1], lines[2], lines[0]]):
            with self.subTest(order=order), self.assertRaisesRegex(ValueError, "final stage"):
                validate_alignment("\n".join(order), self.PROJECT, self.PROJECT)

    def test_missing_duplicate_or_dynamic_runtime_is_rejected(self):
        for dockerfile in (self.BASE.replace("aspnet:10.0", "aspnet:latest"),
                           self.BASE.replace("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS database-worker", ""),
                           self.BASE.replace("AS database-worker", "AS packory")):
            with self.subTest(dockerfile=dockerfile), self.assertRaises(ValueError):
                validate_alignment(dockerfile, self.PROJECT, self.PROJECT)

    def test_multiple_or_malformed_frameworks_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "exactly one"):
            validate_alignment(self.BASE, self.PROJECT,
                               self.PROJECT.replace("</PropertyGroup>", "<TargetFramework>net11.0</TargetFramework></PropertyGroup>"))
        with self.assertRaisesRegex(ValueError, "Cannot parse"):
            validate_alignment(self.BASE, "<Project>", self.PROJECT)


if __name__ == "__main__":
    unittest.main()
