import argparse
import hashlib
import re
import subprocess
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from check_version import PROJECT, ROOT, read_version


def main() -> None:
    parser = argparse.ArgumentParser(description="Verify and package the Release DLL.")
    parser.add_argument("--package-name", required=True)
    parser.add_argument("--output-directory", type=Path, default=Path("artifacts"))
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9._-]+", args.package_name):
        parser.error("Package name may contain only letters, numbers, '.', '_' and '-'")
    assembly = Path(
        subprocess.check_output(
            [
                "dotnet",
                "msbuild",
                str(ROOT / PROJECT),
                "-property:Configuration=Release",
                "-getProperty:TargetPath",
                "-nologo",
            ],
            cwd=ROOT,
            text=True,
            encoding="utf-8",
        ).strip()
    )
    if not assembly.is_file() or assembly.stat().st_size == 0:
        raise SystemExit(
            f"Expected nonempty Release assembly was not produced: {assembly}"
        )
    if assembly.name != "LiveSplit.Bridge.dll":
        raise SystemExit(f"Unexpected Release assembly: {assembly}")
    version = read_version()
    assert version is not None
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(ROOT / "eng/VerifyAssemblyVersion"),
            "--configuration",
            "Release",
            "--",
            str(assembly),
            version,
        ],
        cwd=ROOT,
        check=True,
    )
    output = ROOT / args.output_directory
    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / f"{args.package_name}.zip"
    with ZipFile(archive_path, "w", compression=ZIP_DEFLATED) as archive:
        archive.write(assembly, "LiveSplit.Bridge.dll")
    with ZipFile(archive_path) as archive:
        entries = archive.infolist()
        if (
            len(entries) != 1
            or entries[0].filename != "LiveSplit.Bridge.dll"
            or entries[0].file_size == 0
            or archive.testzip() is not None
        ):
            raise SystemExit(
                "Release ZIP must contain exactly one nonempty LiveSplit.Bridge.dll"
            )
    digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    checksum_path = output / "SHA256SUMS.txt"
    checksum_path.write_text(
        f"{digest}  {archive_path.name}\n", encoding="ascii", newline="\n"
    )
    print("Verified distribution: LiveSplit.Bridge.dll only")
    print(f"Created {archive_path}")
    print(f"Created {checksum_path}")


if __name__ == "__main__":
    main()
