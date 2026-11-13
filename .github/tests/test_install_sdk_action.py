#
# test_install_sdk_action.py
#
# Copyright (C) 2006-2025 wolfSSL Inc.
#
# This file is part of wolfSSL.
#
# wolfSSL is free software; you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation; either version 2 of the License, or
# (at your option) any later version.
#
# wolfSSL is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program; if not, write to the Free Software
# Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1335, USA

# Runs the install-zephyr-sdk download step with a fake curl to check it is idempotent.

import os
import re
import shutil
import subprocess
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
ACTION_YML = REPO_ROOT / ".github" / "actions" / "install-zephyr-sdk" / "action.yml"

SDK_VERSION = "1.0.1"
ARCH = "arm-zephyr-eabi"
SDK_NAME = "zephyr-sdk-" + SDK_VERSION
GCC = SDK_NAME + "/gnu/" + ARCH + "/bin/" + ARCH + "-gcc"
STAMP = ".installed-" + SDK_VERSION + "-" + ARCH

BASE_URL = "https://github.com/zephyrproject-rtos/sdk-ng/releases/download/v" + \
    SDK_VERSION
MINIMAL_NAME = SDK_NAME + "_linux-x86_64_minimal.tar.xz"
TOOLCHAIN_NAME = "toolchain_gnu_linux-x86_64_" + ARCH + ".tar.xz"
MINIMAL_URL = BASE_URL + "/" + MINIMAL_NAME
TOOLCHAIN_URL = BASE_URL + "/" + TOOLCHAIN_NAME



# curl stand-in: serves FAKE_TARBALLS/<basename> for `curl -sSL -O <url>`.
CURL_SHIM = r"""#!/usr/bin/env bash
if [ "$#" -ne 3 ] || [ "$1" != "-sSL" ] || [ "$2" != "-O" ]; then
    echo "curl shim: unsupported invocation: $*" >&2
    exit 64
fi
url="$3"
printf '%s\n' "$url" >> "$CURL_LOG"
name="${url##*/}"
if [ ! -f "$FAKE_TARBALLS/$name" ]; then
    echo "curl shim: $name not present in $FAKE_TARBALLS" >&2
    exit 6
fi
cp -f "$FAKE_TARBALLS/$name" "./$name" || exit 6
"""

# The pre-fix download step, kept as a control that must still fail on a stale tree.
OLD_DOWNLOAD_SCRIPT = """\
mkdir -p "$SDK_DIR"
cd "$SDK_DIR"
curl -sSL -O "%(base)s/zephyr-sdk-${SDK_VERSION}_linux-x86_64_minimal.tar.xz"
curl -sSL -O "%(base)s/toolchain_gnu_linux-x86_64_${ARCH}.tar.xz"
tar -xf "zephyr-sdk-${SDK_VERSION}_linux-x86_64_minimal.tar.xz"
tar -xf "toolchain_gnu_linux-x86_64_${ARCH}.tar.xz"
mkdir -p "zephyr-sdk-$SDK_VERSION/gnu"
mv "$ARCH" "zephyr-sdk-$SDK_VERSION/gnu/"
(cd "zephyr-sdk-$SDK_VERSION" && ./setup.sh -t "$ARCH")
""" % {"base": BASE_URL}

GCC_STUB = "#!/bin/sh\necho 'fake %s-gcc'\nexit 0\n" % ARCH
STALE_GCC_STUB = "#!/bin/sh\necho 'STALE %s-gcc'\nexit 0\n" % ARCH
SETUP_SH_STUB = ("#!/bin/sh\n"
                 "echo \"setup.sh $*\" > setup-ran.log\n"
                 "exit 0\n")


def _make_tarball(out, members):
    """Build a .tar.xz from {relative path: content}; `#!` content is 0o755."""
    stage = out.parent / (out.name + ".stage")
    try:
        for rel, content in members.items():
            path = stage / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
            path.chmod(0o755 if content.startswith("#!") else 0o644)
        tops = sorted({rel.split("/", 1)[0] for rel in members})
        subprocess.run(
            ["tar", "-cJf", str(out), "-C", str(stage)] + tops,
            check=True, capture_output=True)
    finally:
        shutil.rmtree(stage, ignore_errors=True)




@pytest.fixture(scope="module")
def action():
    return yaml.safe_load(ACTION_YML.read_text())


@pytest.fixture(scope="module")
def fake_tarballs(tmp_path_factory):
    """The fake SDK 1.0.1 releases, pre-built with the real tar/xz."""
    d = tmp_path_factory.mktemp("fake-tarballs")
    _make_tarball(d / MINIMAL_NAME, {
        SDK_NAME + "/setup.sh": SETUP_SH_STUB,
        SDK_NAME + "/sdk.sh": "# fake sdk.sh\n",
        SDK_NAME + "/cmake/FindZephyr-sdk.cmake": "# fake cmake module\n",
    })
    _make_tarball(d / TOOLCHAIN_NAME, {
        ARCH + "/bin/" + ARCH + "-gcc": GCC_STUB,
    })
    return d


@pytest.fixture()
def download_step(action):
    """The 'Download and unpack' step of the composite action."""
    for step in action["runs"]["steps"]:
        if step.get("name") == "Download and unpack":
            return step
    pytest.fail("no 'Download and unpack' step in %s" % ACTION_YML)




def _run_step(script, sdk_dir, fake_dir, log, bin_dir):
    """Run a step script under bash -euo pipefail with the curl shim first in PATH."""
    bin_dir.mkdir(parents=True, exist_ok=True)
    shim = bin_dir / "curl"
    shim.write_text(CURL_SHIM)
    shim.chmod(0o755)
    env = {
        "PATH": str(bin_dir) + os.pathsep + os.environ.get("PATH", "")
            + os.pathsep + "/usr/sbin:/usr/bin:/sbin:/bin",
        "SDK_VERSION": SDK_VERSION,
        "SDK_DIR": str(sdk_dir),
        "ARCH": ARCH,
        "FAKE_TARBALLS": str(fake_dir),
        "CURL_LOG": str(log),
        "LC_ALL": "C",
    }
    return subprocess.run(
        [shutil.which("bash") or "bash", "-euo", "pipefail", "-c", script],
        capture_output=True, text=True, env=env)


def _logs(log):
    return log.read_text().splitlines() if log.exists() else []


def _plant_stale_toolchain(sdk_dir):
    """A non-empty gnu/<arch> tree left by a previous install or a partial cache restore."""
    stale = sdk_dir / SDK_NAME / "gnu" / ARCH
    (stale / "bin").mkdir(parents=True)
    (stale / "bin" / (ARCH + "-gcc")).write_text(STALE_GCC_STUB)
    (stale / "junk.txt").write_text("leftover from a partial cache restore\n")
    return stale




def test_download_step_script_shape(download_step, action):
    """The download step must extract with tar -C, keep the stamp and read declared inputs."""
    script = download_step["run"]
    assert "mv " not in script
    assert re.search(r"tar -xf .+ -C", script)
    assert ".installed-$SDK_VERSION-$ARCH" in script
    # The step's env must read inputs the action declares.
    assert download_step["env"]["SDK_VERSION"] == "${{ inputs.version }}"
    assert download_step["env"]["SDK_DIR"] == "${{ inputs.dir }}"
    assert download_step["env"]["ARCH"] == "${{ inputs.arm-toolchain }}"
    for name in ("version", "dir", "arm-toolchain"):
        assert name in action["inputs"]


def test_export_step_still_exports_toolchain_env(download_step, action):
    """The next step must still export ZEPHYR_TOOLCHAIN_VARIANT and ZEPHYR_SDK_INSTALL_DIR."""
    steps = action["runs"]["steps"]
    export = steps[steps.index(download_step) + 1]
    run = export["run"]
    assert 'ZEPHYR_TOOLCHAIN_VARIANT=zephyr' in run
    assert "ZEPHYR_SDK_INSTALL_DIR=" in run
    assert '>> "$GITHUB_ENV"' in run




def test_cold_install(tmp_path, download_step, fake_tarballs):
    """a. Cold: installs the SDK, downloads both archives, leaves no tarball."""
    sdk_dir = tmp_path / "sdk"
    log = tmp_path / "curl.log"
    res = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                    tmp_path / "bin")
    assert res.returncode == 0, res.stdout + res.stderr
    gcc = sdk_dir / GCC
    assert gcc.is_file()
    assert os.access(gcc, os.X_OK)
    assert gcc.read_text() == GCC_STUB
    assert (sdk_dir / STAMP).is_file()
    assert _logs(log) == [MINIMAL_URL, TOOLCHAIN_URL]
    assert not (sdk_dir / MINIMAL_NAME).exists()
    assert not (sdk_dir / TOOLCHAIN_NAME).exists()
    assert (sdk_dir / SDK_NAME / "setup-ran.log").read_text() == \
        "setup.sh -t %s\n" % ARCH


def test_stale_toolchain_tree_cache_miss(tmp_path, download_step,
                                         fake_tarballs):
    """b. Stale tree and cache miss (the reported bug): must succeed with a working tree."""
    sdk_dir = tmp_path / "sdk"
    stale = _plant_stale_toolchain(sdk_dir)
    log = tmp_path / "curl.log"
    res = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                    tmp_path / "bin")
    assert res.returncode == 0, res.stdout + res.stderr
    gcc = sdk_dir / GCC
    assert gcc.is_file()
    assert os.access(gcc, os.X_OK)
    assert gcc.read_text() == GCC_STUB
    assert not (stale / "junk.txt").exists()
    assert (sdk_dir / STAMP).is_file()
    assert _logs(log) == [MINIMAL_URL, TOOLCHAIN_URL]


def test_old_script_fails_on_stale_toolchain_tree(tmp_path, fake_tarballs):
    """b. Control: the old script must still fail on the stale tree."""
    sdk_dir = tmp_path / "sdk"
    _plant_stale_toolchain(sdk_dir)
    res = _run_step(OLD_DOWNLOAD_SCRIPT, sdk_dir, fake_tarballs,
                    tmp_path / "curl.log", tmp_path / "bin")
    assert res.returncode != 0
    lowered = res.stderr.lower()
    assert "directory not empty" in lowered or "cannot overwrite" in lowered


def test_second_run_downloads_nothing(tmp_path, download_step, fake_tarballs):
    """c. Re-run: with the stamp present, succeed and download nothing."""
    sdk_dir = tmp_path / "sdk"
    log = tmp_path / "curl.log"
    first = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                      tmp_path / "bin")
    assert first.returncode == 0, first.stdout + first.stderr
    assert _logs(log) == [MINIMAL_URL, TOOLCHAIN_URL]
    log.write_text("")
    second = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                       tmp_path / "bin")
    assert second.returncode == 0, second.stdout + second.stderr
    assert "already installed" in second.stdout
    assert _logs(log) == []
    assert (sdk_dir / STAMP).is_file()
    assert (sdk_dir / GCC).is_file()


def test_stamp_with_unusable_compiler_is_not_trusted(tmp_path, download_step,
                                                     fake_tarballs):
    """c.2 A stamp whose compiler cannot run must trigger a re-install."""
    sdk_dir = tmp_path / "sdk"
    log = tmp_path / "curl.log"
    first = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                      tmp_path / "bin")
    assert first.returncode == 0, first.stdout + first.stderr

    gcc = sdk_dir / GCC
    gcc.write_text("#!/bin/sh\nexit 7\n")      # present, +x, cannot run
    os.chmod(gcc, 0o755)
    log.write_text("")

    second = _run_step(download_step["run"], sdk_dir, fake_tarballs, log,
                       tmp_path / "bin")
    assert second.returncode == 0, second.stdout + second.stderr
    assert "already installed" not in second.stdout, second.stdout
    assert "stale stamp" in second.stdout, second.stdout
    assert _logs(log) == [MINIMAL_URL, TOOLCHAIN_URL]
    assert gcc.read_text() == GCC_STUB
    assert (sdk_dir / STAMP).is_file()


def test_partial_restore_is_repaired(tmp_path, download_step, fake_tarballs):
    """d. Partial restore with no stamp: rebuild a complete tree."""
    sdk_dir = tmp_path / "sdk"
    broken = sdk_dir / SDK_NAME / "gnu" / ARCH
    (broken / "lib").mkdir(parents=True)
    (broken / "lib" / "libfoo.a").write_text("half-restore\n")
    junk = broken / "old-junk.bin"
    junk.write_text("leftover\n")
    assert not (broken / "bin" / (ARCH + "-gcc")).exists()
    res = _run_step(download_step["run"], sdk_dir, fake_tarballs,
                    tmp_path / "curl.log", tmp_path / "bin")
    assert res.returncode == 0, res.stdout + res.stderr
    gcc = sdk_dir / GCC
    assert gcc.is_file()
    assert os.access(gcc, os.X_OK)
    assert gcc.read_text() == GCC_STUB
    assert not junk.exists()
    assert not (broken / "lib" / "libfoo.a").exists()
    assert (sdk_dir / STAMP).is_file()


def test_broken_toolchain_tarball_fails_without_stamp(tmp_path, download_step,
                                                      fake_tarballs):
    """e. Toolchain archive without a compiler: fail and write no stamp."""
    sdk_dir = tmp_path / "sdk"
    bad = tmp_path / "bad-tarballs"
    bad.mkdir()
    shutil.copy(fake_tarballs / MINIMAL_NAME, bad / MINIMAL_NAME)
    _make_tarball(bad / TOOLCHAIN_NAME,
                  {ARCH + "/README.txt": "no compiler in here\n"})
    res = _run_step(download_step["run"], sdk_dir, bad, tmp_path / "curl.log",
                    tmp_path / "bin")
    assert res.returncode != 0, res.stdout + res.stderr
    assert "compiler missing" in res.stdout
    assert not (sdk_dir / STAMP).exists()
