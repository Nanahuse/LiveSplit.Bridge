import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import check_version
from check_version import PROJECT, check_versions, parse_version, read_version


class VersionChecksTests(unittest.TestCase):
    def test_unchanged_and_increasing_versions_pass(self) -> None:
        check_versions("0.6.0", "0.6.0", [(0, 6, 0)])
        check_versions("0.6.0", "0.5.1", [(0, 5, 1)])
        check_versions("0.6.0", None, [(0, 5, 1)])

    def test_invalid_versions_are_rejected(self) -> None:
        for value in (
            "",
            "0.6",
            "v0.6.0",
            "01.6.0",
            "0.6.0-rc1",
            "0.6.0+sha",
            "65535.0.0",
        ):
            with self.subTest(value=value), self.assertRaises(ValueError):
                parse_version(value)

    def test_numeric_ordering_and_downgrades(self) -> None:
        check_versions("0.10.0", "0.9.0", [(0, 9, 0)])
        with self.assertRaises(ValueError):
            check_versions("0.6.0", "0.7.0", [(0, 5, 1)])
        with self.assertRaises(ValueError):
            check_versions("0.6.0", "0.6.0", [(0, 7, 0)])

    def test_print_version_skips_version_checks(self) -> None:
        with (
            patch.object(check_version, "read_version", return_value="0.6.0"),
            patch.object(sys, "argv", ["check_version.py", "--print-version"]),
            patch.object(check_version, "published_versions") as published,
            patch("builtins.print") as print_mock,
        ):
            check_version.main()
        print_mock.assert_called_once_with("0.6.0")
        published.assert_not_called()

    def test_single_unconditional_version_source(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / PROJECT
            project.parent.mkdir(parents=True)
            with (
                patch.object(check_version, "ROOT", root),
                patch.object(check_version, "git", return_value=PROJECT),
            ):
                project.write_text("<Project/>", encoding="utf-8")
                self.assertIsNone(read_version(allow_missing=True))
                with self.assertRaises(ValueError):
                    read_version()
                for properties in (
                    "<Version>0.6.0</Version><Version>0.6.0</Version>",
                    '<Version Condition="true">0.6.0</Version>',
                    "<Version>0.6.0</Version><AssemblyVersion>0.6.0.0</AssemblyVersion>",
                ):
                    project.write_text(
                        f"<Project><PropertyGroup>{properties}</PropertyGroup></Project>",
                        encoding="utf-8",
                    )
                    with (
                        self.subTest(properties=properties),
                        self.assertRaises(ValueError),
                    ):
                        read_version()
                project.write_text(
                    "<Project><PropertyGroup><Version>0.6.0</Version></PropertyGroup></Project>",
                    encoding="utf-8",
                )
                self.assertEqual(read_version(), "0.6.0")
                props = "Directory.Build.props"
                (root / props).write_text(
                    "<Project><PropertyGroup><FileVersion>1.0.0.0</FileVersion></PropertyGroup></Project>",
                    encoding="utf-8",
                )
                with patch.object(
                    check_version, "git", return_value=f"{PROJECT}\n{props}"
                ):
                    with self.assertRaises(ValueError):
                        read_version()
                source = "src/LiveSplit.Bridge/AssemblyInfo.cs"
                (root / source).write_text(
                    '[assembly: AssemblyVersion("1.0.0.0")]', encoding="utf-8"
                )
                with patch.object(
                    check_version, "git", return_value=f"{PROJECT}\n{source}"
                ):
                    with self.assertRaises(ValueError):
                        read_version()

    def test_all_release_pages_are_checked_and_drafts_ignored(self) -> None:
        pages = [
            [{"tag_name": "v0.1.0", "draft": False}] * 100,
            [
                {"tag_name": "v0.9.0", "draft": False},
                {"tag_name": "v9.0.0", "draft": True},
                {"tag_name": "v9.0.0-rc1", "draft": False},
            ],
        ]
        with patch("check_version.urllib.request.urlopen") as urlopen:
            urlopen.return_value.__enter__.return_value.read.side_effect = [
                json.dumps(page) for page in pages
            ]
            versions = check_version.published_versions("owner/repo")
        self.assertEqual(len(versions), 101)
        self.assertEqual(max(versions), (0, 9, 0))
        self.assertEqual(urlopen.call_count, 2)


if __name__ == "__main__":
    unittest.main()
