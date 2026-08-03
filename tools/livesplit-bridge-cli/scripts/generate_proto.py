from pathlib import Path
import shutil
import sys

from grpc_tools import protoc


ROOT = Path(__file__).resolve().parents[3]
PROTO_ROOT = ROOT / "src" / "LiveSplit.Bridge.Protocol" / "proto"
OUTPUT = Path(__file__).resolve().parents[1] / "src"


def main() -> int:
    proto_files = sorted(PROTO_ROOT.rglob("*.proto"))
    if not proto_files:
        print(f"No proto files found below {PROTO_ROOT}", file=sys.stderr)
        return 1

    generated_root = OUTPUT / "livesplit"
    if generated_root.exists():
        shutil.rmtree(generated_root)

    result = protoc.main(
        [
            "grpc_tools.protoc",
            f"-I{PROTO_ROOT}",
            f"--python_out={OUTPUT}",
            *[str(path) for path in proto_files],
        ]
    )
    if result:
        return result

    for directory in [OUTPUT / "livesplit", OUTPUT / "livesplit/bridge", OUTPUT / "livesplit/bridge/v1"]:
        (directory / "__init__.py").touch()
    print(f"Generated {len(proto_files)} protobuf modules in {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
