"""Prevent framework-dependent Packory images from shipping without their .NET runtime.

An SDK can build a project targeting an older framework, but an ASP.NET runtime
image with a *different major* does not supply that target by default. This is
a static preflight check, not a container execution or vulnerability scan.
"""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DOCKERFILE = ROOT / "Dockerfile"
CSPROJ = ROOT / "src" / "Packory" / "Packory.csproj"

# Reject unsupported/dynamic image references until runtime compatibility can
# be evaluated explicitly; do not silently assume a floating base is safe.
IMAGE = re.compile(
    r"^FROM\s+mcr\.microsoft\.com/dotnet/(sdk|aspnet):"
    r"([1-9][0-9]*)\.[0-9]+(?:\.[0-9]+)?(?:-[a-zA-Z0-9._-]+)?"
    r"(?:@sha256:[a-f0-9]{64})?(?:\s+AS\s+[a-zA-Z0-9._-]+)?\s*$",
    re.IGNORECASE,
)
FRAMEWORK = re.compile(r"net([1-9][0-9]*)\.[0-9]+")


def validate_alignment(dockerfile: str, project: str) -> None:
    try:
        root = ET.fromstring(project)
    except ET.ParseError as error:
        raise ValueError("Cannot parse Packory target framework") from error
    frameworks = [e.text.strip() for e in root.findall(".//TargetFramework") if e.text]
    if len(frameworks) != 1 or not FRAMEWORK.fullmatch(frameworks[0]):
        raise ValueError("Packory must declare exactly one concrete .NET target framework")
    target_major = int(FRAMEWORK.fullmatch(frameworks[0]).group(1))
    images = {}
    for line in dockerfile.splitlines():
        if not re.match(r"^\s*FROM\s+", line, re.IGNORECASE):
            continue
        match = IMAGE.fullmatch(line.strip())
        if not match:
            raise ValueError("Unsupported or unpinned-format .NET base image")
        flavor, version = match.group(1).lower(), int(match.group(2))
        if flavor in images:
            raise ValueError("Multiple .NET build/runtime images require explicit review")
        images[flavor] = version
    if set(images) != {"sdk", "aspnet"}:
        raise ValueError("Packory requires one SDK and one ASP.NET runtime image")
    if images["aspnet"] != target_major:
        raise ValueError("ASP.NET runtime major differs from the target framework; framework-dependent app may not start")
    if images["sdk"] < target_major:
        raise ValueError("SDK major is older than the target framework")


class ContainerRuntimeAlignmentTests(unittest.TestCase):
    PROJECT = '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    BASE = 'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\n'

    def test_repository_dockerfile_matches_project(self):
        validate_alignment(DOCKERFILE.read_text(encoding="utf-8"), CSPROJ.read_text(encoding="utf-8"))

    def test_runtime_major_bump_alone_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "runtime major differs"):
            validate_alignment(self.BASE.replace("aspnet:10.0", "aspnet:11.0"), self.PROJECT)

    def test_newer_sdk_alone_does_not_imply_newer_runtime(self):
        validate_alignment(self.BASE.replace("sdk:10.0", "sdk:11.0"), self.PROJECT)

    def test_missing_or_dynamic_runtime_is_rejected(self):
        for dockerfile in (self.BASE.replace("aspnet:10.0", "aspnet:latest"),
                           self.BASE.replace("FROM mcr.microsoft.com/dotnet/aspnet:10.0", "")):
            with self.subTest(dockerfile=dockerfile), self.assertRaises(ValueError):
                validate_alignment(dockerfile, self.PROJECT)

    def test_old_sdk_and_multiple_frameworks_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "SDK major"):
            validate_alignment(self.BASE.replace("sdk:10.0", "sdk:9.0"), self.PROJECT)
        with self.assertRaisesRegex(ValueError, "exactly one"):
            validate_alignment(self.BASE, self.PROJECT.replace("</PropertyGroup>", "<TargetFramework>net11.0</TargetFramework></PropertyGroup>"))


if __name__ == "__main__":
    unittest.main()
