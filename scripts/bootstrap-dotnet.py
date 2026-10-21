#!/usr/bin/env python3
"""Install the pinned .NET 11 RC2 SDK locally. Python 3.12+, Linux x86_64."""
import hashlib
import json
import os
import platform
import posixpath
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path, PurePosixPath
from urllib.parse import urlparse

VERSION = "11.0.100-rc.2.26504.105"
RELEASE = "11.0.0-rc.2"
BASE = "https://builds.dotnet.microsoft.com/dotnet"
METADATA = BASE + "/release-metadata/11.0/releases.json"
URL = BASE + "/Sdk/" + VERSION + "/dotnet-sdk-" + VERSION + "-linux-x64.tar.gz"
SHA512 = (
    "54e9463339337cef68f455e97dadac0a9"
    "e1b49416a3b15504b577144b24ae4b4eed"
    "4e7ba3bac507650b61edf704e977b4627"
    "d3804f4c2688cc5bd69b4f2eecdd"
)

def response(url):
    result = urllib.request.urlopen(
        urllib.request.Request(url, headers={"User-Agent": "WayScrawl-bootstrap/0.1"}),
        timeout=60)
    final = urlparse(result.geturl())
    if final.scheme != "https" or final.hostname != "builds.dotnet.microsoft.com":
        result.close()
        raise RuntimeError("Unexpected download host.")
    return result

def validate_metadata():
    with response(METADATA) as source:
        data = source.read(8 * 1024 * 1024 + 1)
    if len(data) > 8 * 1024 * 1024:
        raise RuntimeError("Metadata too large.")
    matches = set()
    for release in json.loads(data).get("releases", []):
        if release.get("release-version") != RELEASE:
            continue
        candidates = list(release.get("sdks", []))
        if isinstance(release.get("sdk"), dict):
            candidates.append(release["sdk"])
        for sdk in candidates:
            if sdk.get("version") != VERSION:
                continue
            for item in sdk.get("files", []):
                if item.get("rid") == "linux-x64" and item.get("name") == "dotnet-sdk-linux-x64.tar.gz":
                    matches.add((item.get("url"), item.get("hash", "").lower()))
    if matches != {(URL, SHA512)}:
        raise RuntimeError("Official metadata differs from the committed RC2 pin. Review it explicitly.")

def download(path):
    with response(URL) as source, path.open("wb") as output:
        expected = source.headers.get("Content-Length")
        digest = hashlib.sha512()
        size = 0
        while block := source.read(1024 * 1024):
            size += len(block)
            if size > 1024 * 1024 * 1024:
                raise RuntimeError("SDK download exceeds 1 GiB.")
            digest.update(block)
            output.write(block)
    # http.client returns EOF instead of raising when the server drops the connection.
    if expected is not None and size != int(expected):
        raise ConnectionError(f"Download truncated at {size} of {expected} bytes.")
    return digest.hexdigest()

def sdk_version(directory, cli_home):
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(directory), DOTNET_CLI_HOME=str(cli_home),
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    result = subprocess.run([str(directory / "dotnet"), "--version"], cwd=directory,
                            env=env, capture_output=True, text=True, timeout=60, check=True)
    return result.stdout.strip()

def main():
    if sys.version_info < (3, 12):
        raise RuntimeError("Python 3.12+ is required.")
    if platform.system() != "Linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        raise RuntimeError("This bootstrap intentionally targets Linux x86_64.")
    root = Path(__file__).resolve().parent.parent
    pin = json.loads((root / "global.json").read_text())["sdk"]
    if pin != {"version": VERSION, "rollForward": "disable", "allowPrerelease": True}:
        raise RuntimeError("global.json differs from the bootstrap pin.")
    target = root / ".dotnet"
    lock = root / ".dotnet-bootstrap.lock"
    fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        with os.fdopen(fd, "w") as stream:
            stream.write(str(os.getpid()) + "\n")
        with tempfile.TemporaryDirectory(prefix=".dotnet-bootstrap-", dir=root) as temporary:
            work = Path(temporary)
            cli_home = work / "cli-home"
            cli_home.mkdir()
            if target.is_symlink():
                raise RuntimeError("Refusing a symlink .dotnet target.")
            if target.exists():
                if target.is_dir() and sdk_version(target, cli_home) == VERSION:
                    print("Exact SDK already installed: " + VERSION)
                    return
                raise RuntimeError("Existing .dotnet will not be overwritten.")
            print("Checking official release metadata...")
            validate_metadata()
            archive_path = work / "sdk.tar.gz"
            for attempt in range(1, 4):
                try:
                    actual = download(archive_path)
                    break
                except ConnectionError as error:
                    if attempt == 3:
                        raise
                    print(f"{error} Retrying ({attempt}/3)...")
            if actual != SHA512:
                raise RuntimeError("SDK SHA512 mismatch; archive will not be extracted.")
            payload = work / "payload"
            payload.mkdir(mode=0o700)
            with tarfile.open(archive_path, "r:gz") as archive:
                members = []
                total = 0
                for member in archive:
                    name = PurePosixPath(member.name)
                    if (name.is_absolute() or ".." in name.parts or "\\" in member.name or
                        not (member.isfile() or member.isdir() or member.issym()) or member.size < 0):
                        raise RuntimeError("Unsafe or unsupported tar member: " + member.name)
                    if member.issym():
                        # The SDK deduplicates resource assemblies with relative symlinks.
                        link = posixpath.normpath(posixpath.join(str(name.parent), member.linkname))
                        if member.linkname.startswith("/") or link == ".." or link.startswith("../"):
                            raise RuntimeError("Symlink leaves the SDK root: " + member.name)
                    members.append(member)
                    total += member.size
                    if len(members) > 100_000 or total > 4 * 1024 * 1024 * 1024:
                        raise RuntimeError("SDK extraction limit exceeded.")
                archive.extractall(payload, members=members, filter="data")
            if sdk_version(payload, cli_home) != VERSION:
                raise RuntimeError("Extracted SDK reports a different version.")
            if target.exists() or target.is_symlink():
                raise RuntimeError("Target appeared during bootstrap.")
            os.rename(payload, target)
            print("Installed exact SDK " + VERSION + " into " + str(target))
    finally:
        lock.unlink(missing_ok=True)

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
    except Exception as error:
        sys.exit("SDK bootstrap failed: " + str(error))
