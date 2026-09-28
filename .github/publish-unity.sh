#!/usr/bin/env bash
# Pack and publish a signed Unity package tarball.
# Usage: publish-unity.sh <npm-dist-tag>
# Empty, null, false, or undefined selects the latest tag.
set -euo pipefail

CHANNEL="${1:-}"
if [[ -z "${CHANNEL}" || "${CHANNEL}" == "null" || "${CHANNEL}" == "false" || "${CHANNEL}" == "undefined" ]]; then
  TAG="latest"
else
  # npm dist-tags cannot contain "/". prerelease/alpha -> alpha
  TAG="${CHANNEL##*/}"
fi

# Install the official UPM CLI (adds `upm` to PATH via shell profile,
# which a non-interactive CI shell does NOT pick up — so resolve the
# binary directly below).
# https://docs.unity3d.com/6000.3/Documentation/Manual/upm-cli.html
curl -fsSL https://cdn.packages.unity.com/upm-cli/install.sh | bash

UPM_BIN="$(command -v upm || true)"
if [ -z "${UPM_BIN}" ]; then
  UPM_BIN="$(find "${HOME}" -type f -name upm 2>/dev/null | head -n1)"
fi
if [ -z "${UPM_BIN}" ]; then
  echo "::error::Could not locate the 'upm' binary after install." >&2
  exit 1
fi

"${UPM_BIN}" --version

# Packs the package in the repo root and writes the SIGNED tarball to ./dist.
# Service account must hold the "Package Manager Package Signer" role.
"${UPM_BIN}" pack . --organization-id "${UNITY_ORGANIZATION_ID}" --destination ./dist

TARBALL="$(ls ./dist/*.tgz | head -n1)"
echo "Publishing ${TARBALL} on dist-tag ${TAG}"
npm publish "${TARBALL}" --access public --provenance --tag "${TAG}"
