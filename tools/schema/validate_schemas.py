import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent
SCHEMA_FILES = {
    "request": "rpc-request.schema.json",
    "response": "rpc-response.schema.json",
    "event": "bridge-event.schema.json",
}


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate JSON v4 schema contracts.")
    parser.add_argument("output_dir", nargs="?", type=Path, default=ROOT / "out")
    args = parser.parse_args()
    validators = {}
    for kind, filename in SCHEMA_FILES.items():
        schema = json.loads((args.output_dir / filename).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        validators[kind] = Draft202012Validator(schema)

    sources = {
        "fixture": ROOT / "samples" / "validation-cases.json",
        "csharp": args.output_dir / "csharp-serialized-cases.json",
    }
    counts = {}
    failures = 0
    for source, path in sources.items():
        cases = json.loads(path.read_text(encoding="utf-8"))
        passes = 0
        for case in cases:
            errors = list(validators[case["schema"]].iter_errors(case["data"]))
            passed = (not errors) == case["valid"]
            print(
                f"{'PASS' if passed else 'FAIL'} "
                f"{source}/{case['schema']}: {case['name']}"
            )
            if passed:
                passes += 1
            else:
                failures += 1
                for error in errors:
                    print(f"  {error.json_path}: {error.message}")
        counts[source] = (passes, len(cases))

    print(f"Draft 2020-12 schema documents compiled: {len(validators)}/3")
    for source, label in (
        ("fixture", "Protocol constraint fixtures"),
        ("csharp", "Actual C# serialized messages"),
    ):
        passes, total = counts[source]
        print(f"{label}: {passes}/{total} passed")
    return int(failures > 0)


if __name__ == "__main__":
    raise SystemExit(main())
