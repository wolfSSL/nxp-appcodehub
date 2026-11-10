#!/usr/bin/env bash
# Build every app (any dir with sample.yaml/testcase.yaml) with Twister --build-only; -h for usage.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

BOARD="${BOARD:-frdm_mcxn947/mcxn947/cpu0}"
OUTDIR="${OUTDIR:-${REPO_ROOT}/twister-out}"
JOBS="${JOBS:-$(nproc 2>/dev/null || echo 4)}"
EXTRA_ARGS=()

usage() {
    cat <<'USAGE'
Usage: .github/scripts/run_twister_apps.sh [-b BOARD] [-o OUTDIR] [-j N] [-- extra twister args]

Environment:
  BOARD                 board to build for (default frdm_mcxn947/mcxn947/cpu0),
                        overridden by -b
  ZEPHYR_BASE           Zephyr tree; inferred from the west workspace if unset
  ZEPHYR_SDK_INSTALL_DIR Zephyr SDK; set if present at the usual locations
  TWISTER               path to the twister script (default $ZEPHYR_BASE/scripts/twister)
  JOBS                  parallelism (-j), default: number of CPUs
  EXTRA_TWISTER_ARGS    extra arguments, space separated
  SKIP_TWISTER_DEPS_CHECK=1  skip the "can this python run twister" preflight

Output: $OUTDIR/twister.json, $OUTDIR/twister.xml, one build directory per
app/test, plus $OUTDIR/twister.log (raw console log).
USAGE
    exit "${1:-0}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        -b|--board)  BOARD="$2"; shift 2 ;;
        -o|--output) OUTDIR="$2"; shift 2 ;;
        -j|--jobs)   JOBS="$2"; shift 2 ;;
        -h|--help)   usage 0 ;;
        --)          shift; EXTRA_ARGS+=("$@"); break ;;
        *)           EXTRA_ARGS+=("$1"); shift ;;
    esac
done

if [ -z "${ZEPHYR_BASE:-}" ]; then
    for candidate in "${REPO_ROOT}/zephyr" "${REPO_ROOT}/../zephyr" "$(pwd)/zephyr"; do
        if [ -f "${candidate}/CMakeLists.txt" ] && [ -d "${candidate}/scripts" ]; then
            resolved="$(cd "${candidate}" && pwd)"
            export ZEPHYR_BASE="${resolved}"
            break
        fi
    done
fi
if [ -z "${ZEPHYR_BASE:-}" ]; then
    echo "ERROR: ZEPHYR_BASE is not set and no zephyr/ tree was found next to" \
         "${REPO_ROOT}. Run 'west init -l nxp-appcodehub && west update' first," \
         "or export ZEPHYR_BASE." >&2
    exit 2
fi

if [ -z "${ZEPHYR_SDK_INSTALL_DIR:-}" ]; then
    for candidate in /opt/sdk-install/zephyr-sdk-* "${HOME}/.local/zephyr-sdk-*" \
                     /opt/zephyr-sdk-* "${ZEPHYR_BASE}/../zephyr-sdk-*"; do
        if [ -d "${candidate}" ]; then
            export ZEPHYR_SDK_INSTALL_DIR="${candidate}"
            break
        fi
    done
fi

TWISTER="${TWISTER:-${ZEPHYR_BASE}/scripts/twister}"
if [ ! -f "${TWISTER}" ]; then
    echo "ERROR: twister script not found at ${TWISTER}" >&2
    exit 2
fi

PYTHON="${PYTHON:-python3}"
if [ "${SKIP_TWISTER_DEPS_CHECK:-0}" != "1" ] &&
   ! "${PYTHON}" -c 'import yaml, elftools' >/dev/null 2>&1; then
    echo "ERROR: python packages required by twister are missing for ${PYTHON}." >&2
    echo "       Run: pip install -r \${ZEPHYR_BASE}/scripts/requirements.txt" >&2
    echo "       (or set SKIP_TWISTER_DEPS_CHECK=1 to skip this preflight)" >&2
    exit 2
fi

mapfile -t APP_DIRS < <(
    cd "${REPO_ROOT}" && find . \
        \( -path './twister-out*' -o -path './.git*' -o -path './modules*' \
           -o -path './zephyr*' -o -path './node_modules*' \) -prune -o \
        \( -name sample.yaml -o -name testcase.yaml \) -print \
        | sed -e 's|/[^/]*$||' -e 's|^\./||' | sort -u
)

if [ "${#APP_DIRS[@]}" -eq 0 ]; then
    echo "ERROR: no sample.yaml / testcase.yaml found under ${REPO_ROOT}" >&2
    exit 1
fi

TWISTER_ARGS=(--build-only --inline-logs -M all -p "${BOARD}"
              -j "${JOBS}" -O "${OUTDIR}")
# Lint suite ids first: a duplicate id makes Twister abort with a confusing error.
if [ -x "${SCRIPT_DIR}/check_twister_ids.py" ] || [ -f "${SCRIPT_DIR}/check_twister_ids.py" ]; then
    "${PYTHON}" "${SCRIPT_DIR}/check_twister_ids.py" "${REPO_ROOT}" || exit 1
fi
for app in "${APP_DIRS[@]}"; do
    TWISTER_ARGS+=(-T "${REPO_ROOT}/${app}")
done
if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
    TWISTER_ARGS+=("${EXTRA_ARGS[@]}")
fi

echo "ZEPHYR_BASE=${ZEPHYR_BASE}"
echo "ZEPHYR_SDK_INSTALL_DIR=${ZEPHYR_SDK_INSTALL_DIR:-<cmake default>}"
echo "BOARD=${BOARD}"
echo "apps (${#APP_DIRS[@]}): ${APP_DIRS[*]}"
echo "twister: ${TWISTER} ${TWISTER_ARGS[*]}"
echo

mkdir -p "${OUTDIR}"
"${PYTHON}" "${TWISTER}" "${TWISTER_ARGS[@]}" 2>&1 | tee "${OUTDIR}/twister.log"
rc=${PIPESTATUS[0]}

# Copy the summary under the board name so matrix legs don't overwrite each other.
if [ -f "${OUTDIR}/twister.json" ]; then
    cp "${OUTDIR}/twister.json" "${OUTDIR}/twister-${BOARD//\//_}.json"
fi

exit "${rc}"
