#!/usr/bin/env bash
# Environment check for ScreenFlow (task T00).
#
# Requires a .NET 8 SDK and Python 3.11 or newer. A missing prerequisite is not a code problem, so
# this script only reports: it never installs anything and never relaxes the requirement.
# Exit code 0 = ready, 1 = something is missing.

set -u

missing=()

echo '== dotnet --list-sdks =='
if ! command -v dotnet >/dev/null 2>&1; then
    echo '  (dotnet not found on PATH)'
    missing+=('.NET 8 SDK (dotnet CLI not found on PATH)')
else
    sdks="$(dotnet --list-sdks 2>/dev/null || true)"
    if [ -z "$sdks" ]; then
        echo '  (dotnet --list-sdks returned nothing)'
        missing+=('.NET 8 SDK (dotnet --list-sdks returned nothing)')
    else
        printf '  %s\n' "$sdks"
        if ! printf '%s\n' "$sdks" | grep -qE '^[[:space:]]*8\.'; then
            installed="$(printf '%s\n' "$sdks" | awk '{print $1}' | paste -sd, -)"
            missing+=(".NET 8 SDK (installed SDKs: ${installed})")
        fi
    fi
fi

echo
echo '== python --version =='
python_cmd=''
python_text=''
for candidate in python3 python py; do
    if ! command -v "$candidate" >/dev/null 2>&1; then
        continue
    fi
    out="$("$candidate" --version 2>&1 || true)"
    case "$out" in
        Python\ [0-9]*)
            python_cmd="$candidate"
            python_text="$out"
            break
            ;;
    esac
done

if [ -z "$python_cmd" ]; then
    echo '  (python not found on PATH)'
    missing+=('Python 3.11+ (python not found on PATH)')
else
    echo "  ${python_text}  [${python_cmd}]"
    version="${python_text#Python }"
    major="${version%%.*}"
    rest="${version#*.}"
    minor="${rest%%.*}"
    if [ "$major" -lt 3 ] || { [ "$major" -eq 3 ] && [ "$minor" -lt 11 ]; }; then
        missing+=("Python 3.11+ (found ${major}.${minor})")
    fi
fi

echo
if [ "${#missing[@]}" -gt 0 ]; then
    echo 'Environment check FAILED. Missing:'
    for item in "${missing[@]}"; do
        echo "  - ${item}"
    done
    exit 1
fi

echo 'Environment check passed.'
exit 0
