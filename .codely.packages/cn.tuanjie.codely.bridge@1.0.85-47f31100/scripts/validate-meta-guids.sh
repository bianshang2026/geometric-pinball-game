#!/usr/bin/env sh
# Validate Unity GUIDs only; release source policy is a separate Node command.
set -eu
exec node "$(dirname "$0")/release-tools/validate.cjs" --guids-only