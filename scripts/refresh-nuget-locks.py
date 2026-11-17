#!/usr/bin/env python3
"""Resolve every consuming project and publish RID after a NuGet update."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
RIDS = ("win-x64", "linux-x64", "osx-x64", "osx-arm64")


def restore(project, *properties):
    subprocess.run(
        ["dotnet", "restore", str(project), "--force-evaluate",
         "-p:RestoreLockedMode=false", *properties],
        cwd=ROOT, check=True,
    )


def refresh():
    restore("MediaServer.sln")
    # Benchmark is outside the solution but consumes Services and Infrastructure.
    for project in sorted((ROOT / "backend/tools").glob("*/*.csproj")):
        restore(project)
    for rid in RIDS:
        for project in sorted((ROOT / "backend/src").glob("*/*.csproj")):
            target = ROOT / "backend/locks" / rid / project.stem / "packages.lock.json"
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(project.parent / "packages.lock.json", target)
        restore("backend/src/Api/Api.csproj", "-p:RuntimeIdentifier=" + rid)
    subprocess.run(["dotnet", "restore", "MediaServer.sln", "--locked-mode"], cwd=ROOT, check=True)
    subprocess.run(["python3", "scripts/check-release-locks.py"], cwd=ROOT, check=True)


def bundle(output):
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    pr = event["pull_request"]
    result = subprocess.run(
        ["git", "diff", "--name-only", "-z"], cwd=ROOT, check=True, capture_output=True,
    )
    files = []
    for name in result.stdout.decode("utf-8").split("\0"):
        if not name:
            continue
        if not name.startswith("backend/") or not name.endswith("/packages.lock.json"):
            raise RuntimeError("Restore unexpectedly changed " + name)
        files.append({"path": name, "content": (ROOT / name).read_text(encoding="utf-8")})
    Path(output).write_text(json.dumps({
        "pr": pr["number"], "sha": pr["head"]["sha"], "files": files,
    }), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle")
    args = parser.parse_args()
    refresh()
    if args.bundle:
        bundle(args.bundle)
