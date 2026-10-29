"""Render benchmark JSON as a reviewable matrix; never certify software fallback as GPU evidence."""
import argparse
import json
from pathlib import Path


def summarize(paths, require_hardware=False):
    rows = []
    for path in paths:
        report = json.loads(Path(path).read_text(encoding="utf-8"))
        requested = report.get("requestedEncoder")
        actual = report.get("actualEncoder")
        passed = report.get("status") == "passed"
        if require_hardware:
            passed = passed and (
                requested in {"h264_nvenc", "h264_qsv", "h264_vaapi", "h264_amf"}
                and actual == requested
                and report.get("softwareFallback") is False
                and any(c.get("encoder") == requested and c.get("available") is True
                        for c in report.get("capabilities", []))
            )
        rows.append({"scenario": report.get("scenario", "unknown"),
                     "platform": report.get("platform", "unknown"),
                     "requested": requested or "unknown", "actual": actual or "none",
                     "seconds": report.get("elapsedSeconds", "unavailable"),
                     "passed": bool(passed)})
    if not rows:
        raise ValueError("No benchmark reports supplied.")
    return rows


def markdown(rows):
    def cell(value):
        return str(value).replace("|", "\\|").replace("\n", " ").replace("\r", " ")
    lines = ["| Scenario | Platform | Requested | Actual | Preparation seconds | Result |",
             "| --- | --- | --- | --- | --- | --- |"]
    for row in rows:
        lines.append("| " + " | ".join(cell(row[key]) for key in
                     ("scenario", "platform", "requested", "actual", "seconds"))
                     + " | " + ("Passed" if row["passed"] else "Failed / unavailable") + " |")
    return "\n".join(lines) + "\n\nSynthetic encoding evidence only; no physical display or screen-reader certification.\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", nargs="+", type=Path)
    parser.add_argument("--require-hardware", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    rows = summarize(args.reports, args.require_hardware)
    text = markdown(rows)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
    print(text, end="")
    return 0 if all(row["passed"] for row in rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
