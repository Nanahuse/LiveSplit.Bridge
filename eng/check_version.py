import argparse
import json
import os
import re
import subprocess
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = "src/LiveSplit.Bridge/LiveSplit.Bridge.csproj"
VERSION_PATTERN = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
OVERRIDES = {
    "VersionPrefix",
    "VersionSuffix",
    "AssemblyVersion",
    "FileVersion",
    "InformationalVersion",
    "GenerateAssemblyInfo",
    "GenerateAssemblyVersionAttribute",
    "GenerateAssemblyFileVersionAttribute",
    "GenerateAssemblyInformationalVersionAttribute",
}


def git(*args: str) -> str:
    return subprocess.check_output(
        ["git", *args], cwd=ROOT, text=True, encoding="utf-8"
    )


def parse_version(value: str) -> tuple[int, ...]:
    if not re.fullmatch(VERSION_PATTERN, value):
        raise ValueError(f"Expected MAJOR.MINOR.PATCH, got {value!r}")
    parts = tuple(map(int, value.split(".")))
    if any(part > 65534 for part in parts):
        raise ValueError(f"Version cannot be represented in AssemblyVersion: {value}")
    return parts


def read_version(ref: str | None = None, *, allow_missing: bool = False) -> str | None:
    paths = (
        git("ls-tree", "-r", "--name-only", ref).splitlines()
        if ref
        else git("ls-files").splitlines()
    )

    def read(path: str) -> str:
        if ref:
            return git("show", f"{ref}:{path}")
        return (ROOT / path).read_text(encoding="utf-8-sig")

    definitions = []
    for path in paths:
        if path.startswith("external/"):
            continue
        if path == PROJECT or path.endswith((".props", ".targets")):
            document = ET.fromstring(read(path))
            for group in document.iter():
                if group.tag.rsplit("}", 1)[-1] != "PropertyGroup":
                    continue
                for prop in group:
                    name = prop.tag.rsplit("}", 1)[-1]
                    if name == "Version":
                        if (
                            path != PROJECT
                            or group.get("Condition")
                            or prop.get("Condition")
                        ):
                            raise ValueError(
                                f"Version must be unconditional in {PROJECT}: {path}"
                            )
                        definitions.append((prop.text or "").strip())
                    elif name in OVERRIDES:
                        raise ValueError(
                            f"Independent version override {name} in {path}"
                        )
        if path.startswith("src/LiveSplit.Bridge/") and path.endswith(".cs"):
            if re.search(
                r"\[\s*assembly\s*:\s*(?:System\.Reflection\.)?"
                r"Assembly(?:Version|FileVersion|InformationalVersion)(?:Attribute)?\s*\(",
                read(path),
            ):
                raise ValueError(f"Independent assembly version attribute in {path}")
    if not definitions and allow_missing:
        return None
    if len(definitions) != 1:
        raise ValueError(f"Expected exactly one explicit Version in {PROJECT}")
    parse_version(definitions[0])
    return definitions[0]


def published_versions(repository: str) -> list[tuple[int, ...]]:
    versions = []
    page = 1
    while True:
        request = urllib.request.Request(
            f"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}",
            headers={
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "LiveSplit.Bridge-version-check",
            },
        )
        token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
        if token:
            request.add_header("Authorization", f"Bearer {token}")
        with urllib.request.urlopen(request, timeout=30) as response:
            releases = json.load(response)
        for release in releases:
            tag = release["tag_name"]
            if not release["draft"] and re.fullmatch(f"v{VERSION_PATTERN}", tag):
                versions.append(parse_version(tag[1:]))
        if len(releases) < 100:
            return versions
        page += 1


def check_versions(
    current: str,
    previous: str | None,
    published: list[tuple[int, ...]],
    release_tag: str | None = None,
) -> None:
    version = parse_version(current)
    if (
        previous is not None
        and current != previous
        and version <= parse_version(previous)
    ):
        raise ValueError(f"Version must increase when changed: {previous} -> {current}")
    if published and version < max(published):
        raise ValueError(
            f"Version {current} is older than the latest published release"
        )
    if release_tag is not None:
        if not re.fullmatch(f"v{VERSION_PATTERN}", release_tag):
            raise ValueError(f"Expected release tag vMAJOR.MINOR.PATCH: {release_tag}")
        if release_tag != f"v{current}":
            raise ValueError(
                f"Tag {release_tag} does not match product Version {current}"
            )
        if published and version <= max(published):
            raise ValueError(
                f"Release {release_tag} must be newer than all published releases"
            )


def main() -> None:
    parser = argparse.ArgumentParser(description="Check the Bridge product version.")
    parser.add_argument("--base-ref")
    parser.add_argument("--release-tag")
    parser.add_argument(
        "--repository",
        default=os.environ.get("GITHUB_REPOSITORY", "Nanahuse/LiveSplit.Bridge"),
    )
    args = parser.parse_args()
    try:
        current = read_version()
        assert current is not None
        previous = (
            read_version(args.base_ref, allow_missing=True) if args.base_ref else None
        )
        check_versions(
            current, previous, published_versions(args.repository), args.release_tag
        )
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error
    print(f"Product Version: {current}; version checks passed")


if __name__ == "__main__":
    main()
