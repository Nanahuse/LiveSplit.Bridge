import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SCHEMA_NAMES = (
    "rpc-request.schema.json",
    "rpc-response.schema.json",
    "bridge-event.schema.json",
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Synchronize public JSON v4 schemas.")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    schema_dir = ROOT.parents[1] / "schemas" / "v4"
    for name in SCHEMA_NAMES:
        generated = ROOT / "out" / name
        checked_in = schema_dir / name
        if not generated.is_file():
            raise SystemExit(f"Generated schema is missing: {generated}")
        data = generated.read_bytes()
        if args.check:
            if not checked_in.is_file():
                raise SystemExit(f"Checked-in schema is missing: {checked_in}")
            if data != checked_in.read_bytes():
                raise SystemExit(
                    f"Generated schema differs byte-for-byte from {checked_in}"
                )
            print(f"MATCH {name}")
        else:
            checked_in.write_bytes(data)
            print(f"UPDATED {checked_in}")


if __name__ == "__main__":
    main()
