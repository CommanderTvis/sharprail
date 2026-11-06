"""Print the ManagedRuntimes pin entries for .NET SDK versions, after checking the publisher's SHA-512.
Run with: uv run --no-project python scripts/pin-dotnet-sdk.py 10.0.401 10.0.403 [--rids linux-x64 linux-arm64 osx-arm64 osx-x64 win-x64]

For each SDK version and platform it reads the official release metadata, downloads the archive into a temporary
directory, requires the publisher's SHA-512 to match, and prints a C# entry with our own SHA-256 (the value the host
verifies before extraction). Review the printed entries, then paste them into
src/SharpRail.Host.Core/Language/ManagedRuntimes.cs. The downloads are deleted when the script ends.
"""
import argparse, hashlib, json, sys, tempfile, urllib.request
from pathlib import Path

PLATFORM = {'linux-x64': 'linux-x86_64', 'linux-arm64': 'linux-aarch64', 'osx-arm64': 'macos-aarch64', 'osx-x64': 'macos-x86_64', 'win-x64': 'windows-x86_64'}
HOST = 'https://builds.dotnet.microsoft.com/'

parser = argparse.ArgumentParser()
parser.add_argument('versions', nargs='+')
parser.add_argument('--rids', nargs='+', default=list(PLATFORM))
arguments = parser.parse_args()


def fetch(url, destination=None):
    if not url.startswith(HOST):
        sys.exit(f'refusing {url}: not on {HOST}')
    with urllib.request.urlopen(url, timeout=600) as response:
        if destination is None:
            return response.read()
        with open(destination, 'wb') as output:
            while chunk := response.read(1 << 20):
                output.write(chunk)


with tempfile.TemporaryDirectory() as scratch:
    metadata = {}
    for version in arguments.versions:
        channel = '.'.join(version.split('.')[:2])
        if channel not in metadata:
            metadata[channel] = json.loads(fetch(f'{HOST}dotnet/release-metadata/{channel}/releases.json'))
        found = None
        for release in metadata[channel]['releases']:
            for sdk in [release.get('sdk', {}), *release.get('sdks', [])]:
                if sdk.get('version') == version:
                    found = (release['release-date'], sdk)
        if found is None:
            sys.exit(f'{version} is not in the {channel} release metadata')
        date, sdk = found
        for rid in arguments.rids:
            entry = next((f for f in sdk['files'] if f['rid'] == rid and f['name'].startswith('dotnet-sdk-') and f['name'].endswith(('.tar.gz', '.zip'))), None)
            if entry is None:
                sys.exit(f'{version} has no SDK archive for {rid}')
            archive = Path(scratch) / f'{version}-{rid}'
            fetch(entry['url'], archive)
            data = archive.read_bytes()
            if hashlib.sha512(data).hexdigest() != entry['hash'].lower():
                sys.exit(f'{version} {rid}: publisher SHA-512 does not match; nothing printed for it')
            print(f'    new("dotnet-sdk", "{version}", "{PLATFORM[rid]}", "{entry["url"]}",')
            print(f'        "{hashlib.sha256(data).hexdigest()}", "{entry["hash"].lower()}", "{"zip" if entry["name"].endswith(".zip") else "tar.gz"}",')
            print(f'        "SHA-512 from {HOST}dotnet/release-metadata/{version.rsplit(".", 1)[0]}/releases.json (release {date}), verified against the download; SHA-256 computed from that download"),')
            archive.unlink()
