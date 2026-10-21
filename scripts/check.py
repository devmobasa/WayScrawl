#!/usr/bin/env python3
"""Run managed build/checks. This script does not validate a live compositor."""
from pathlib import Path
import os
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
candidate = root / ".dotnet" / "dotnet"
dotnet = str(candidate) if candidate.is_file() else shutil.which("dotnet")
if not dotnet:
    sys.exit("Install the pinned SDK with: python3 scripts/bootstrap-dotnet.py")
env = os.environ.copy()
if candidate.is_file():
    env["DOTNET_ROOT"] = str(candidate.parent)
env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
for arguments in [
    ["--version"],
    ["build", "WayScrawl.slnx", "-c", "Release"],
    ["run", "--project", "tests/WayScrawl.Checks", "-c", "Release", "--no-build"],
]:
    subprocess.run([dotnet, *arguments], cwd=root, env=env, check=True)
