"""Require reviewed immutable bases while allowing references to previous build stages."""
import re
from pathlib import Path

EXPECTED = {"node", "mcr.microsoft.com/dotnet/sdk", "mcr.microsoft.com/dotnet/aspnet"}


def verify(text):
    stages = set()
    bases = []
    for line in text.splitlines():
        if not re.match(r"^\s*FROM\s", line, re.IGNORECASE):
            continue
        tokens = line.split()
        if len(tokens) < 2 or tokens[1].startswith("--"):
            raise ValueError("Review FROM options explicitly before adding them.")
        base = tokens[1]
        if base.lower() not in stages and base.lower() != "scratch":
            match = re.fullmatch(r"([^:@]+(?:/[^:@]+)*):([^@]+)@sha256:[0-9a-f]{64}", base)
            if not match or match[1] not in EXPECTED or match[2].lower() == "latest":
                raise ValueError("Every external base must use a reviewed tag and SHA-256 digest.")
            bases.append(base)
        if len(tokens) > 2:
            if len(tokens) != 4 or tokens[2].lower() != "as":
                raise ValueError("Unexpected FROM syntax.")
            if tokens[3].lower() in stages:
                raise ValueError("Duplicate build stage.")
            stages.add(tokens[3].lower())
    if {base.split(":", 1)[0] for base in bases} != EXPECTED:
        raise ValueError("Missing reviewed base; update the policy when architecture changes.")
    return bases


if __name__ == "__main__":
    for image in verify(Path("docker/Dockerfile").read_text(encoding="utf-8")):
        print(image)
