#!/usr/bin/env sh
# Replace invalid GUIDs in .meta files with proper 32-char lowercase hex.
# Works on macOS (/dev/urandom) and Windows (Git Bash).
#
# Usage:
#   ./scripts/fix-meta-guids.sh           # fix invalid GUIDs and stage changes
#   ./scripts/fix-meta-guids.sh --check   # dry-run (show what would change)
#   ./scripts/fix-meta-guids.sh --no-stage # fix but do not git add changed files
set -eu
cd "$(dirname "$0")/.." || exit 1

MODE="fix"
AUTO_STAGE=true

for arg in "$@"; do
  case "$arg" in
    --check)
      MODE="check"
      ;;
    --fix)
      MODE="fix"
      ;;
    --no-stage)
      AUTO_STAGE=false
      ;;
    *)
      echo "Unknown option: $arg"
      echo "Usage:"
      echo "  ./scripts/fix-meta-guids.sh"
      echo "  ./scripts/fix-meta-guids.sh --check"
      echo "  ./scripts/fix-meta-guids.sh --no-stage"
      exit 2
      ;;
  esac
done

gen_guid() {
  od -An -tx1 -N16 /dev/urandom | tr -d ' \n' | head -c 32
}

invalid_count=0
fixed_count=0

tmp_list=".fix-meta-guids.list.$$"
find . -name "*.meta" -not -path "./.git/*" -not -path "*/build/*" -not -path "*/obj/*" > "$tmp_list"

while IFS= read -r f; do
  [ -z "$f" ] && continue
  guid=$(awk '/^guid:/ { gsub(/\r/, "", $2); print $2 }' "$f")
  [ -z "$guid" ] && continue
  case "$guid" in
    [a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9][a-f0-9])
      continue
      ;;
  esac

  invalid_count=$((invalid_count + 1))
  new_guid=$(gen_guid)
  if [ "$MODE" = "fix" ]; then
    awk -v ng="$new_guid" '/^guid:/ { print "guid: " ng; next } { print }' "$f" > "$f.tmp"
    mv "$f.tmp" "$f"
    if [ "$AUTO_STAGE" = "true" ]; then
      git add -- "$f"
    fi
    echo "FIXED $f: $guid -> $new_guid"
    fixed_count=$((fixed_count + 1))
  else
    echo "INVALID $f: $guid (would fix to $new_guid)"
  fi
done < "$tmp_list"

rm -f "$tmp_list"

if [ "$invalid_count" -eq 0 ]; then
  echo "All .meta GUIDs are valid."
  exit 0
fi

if [ "$MODE" = "check" ]; then
  echo ""
  echo "Found $invalid_count invalid .meta GUID(s)."
  echo "Run to fix:"
  echo "  ./scripts/fix-meta-guids.sh"
  exit 1
fi

echo ""
echo "Fixed $fixed_count invalid .meta GUID(s)."
if [ "$AUTO_STAGE" = "true" ]; then
  echo "Staged fixed .meta files automatically."
else
  echo "Remember to stage fixed files manually."
fi
echo "You can run git commit again."
