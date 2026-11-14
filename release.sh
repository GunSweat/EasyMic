#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"

if [ -z "${version}" ]; then
    echo "Verwendung: $0 <version>  (z. B. 1.2.3)" >&2
    exit 1
fi

version="${version#v}"

if ! [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Ungültige Version '${version}' – erwartet wird x.y.z" >&2
    exit 1
fi

tag="v${version}"

if git rev-parse "${tag}" >/dev/null 2>&1; then
    echo "Tag ${tag} existiert bereits." >&2
    exit 1
fi

git tag -a "${tag}" -m "EasyMic ${tag}"
git push origin "${tag}"

echo "Release ${tag} gestartet."
echo "Status: https://github.com/GunSweat/EasyMic/actions/workflows/release.yml"
