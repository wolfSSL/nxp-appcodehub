#
# test_check_twister_ids.py
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

# Tests for check_twister_ids.py, imported and run as a CLI.

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / ".github" / "scripts" / "check_twister_ids.py"

GOOD_BOARD = "frdm_mcxn947/mcxn947/cpu0"
GOOD_BOARD_NS = "frdm_mcxn947/mcxn947/cpu0/ns"



def load_lint_module():
    """Import .github/scripts/check_twister_ids.py by path (its own module copy)."""
    spec = importlib.util.spec_from_file_location("check_twister_ids", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def lint_module():
    return load_lint_module()


def run_cli(*args, cwd=None):
    """Run the linter as a subprocess with the given CLI arguments."""
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args],
        capture_output=True, text=True, cwd=cwd,
    )


def sample_yaml(name=None, sid="sample.wolfssl.app",
                allow=None, integration=None, allow_raw=None):
    """Render a tiny but complete Twister sample.yaml (name=None omits name:)."""
    if allow_raw is not None:
        allow_block = "    platform_allow: %s" % allow_raw
    else:
        allow_block = "    platform_allow:\n" + "\n".join(
            "      - %s" % b for b in (allow or []))
    integ_block = "    integration_platforms:\n" + "\n".join(
        "      - %s" % b for b in (integration or []))
    name_line = "  name: %s\n" % name if name is not None else ""
    return (
        "sample:\n"
        "  description: A fake test app\n"
        "%s"
        "tests:\n"
        "  %s:\n"
        "    build_only: true\n"
        "    tags:\n"
        "      - net\n"
        "%s\n"
        "%s\n"
    ) % (name_line, sid, allow_block, integ_block)


def make_app(root, dirname, content):
    app = Path(root) / dirname
    app.mkdir(parents=True, exist_ok=True)
    (app / "sample.yaml").write_text(content)
    return app / "sample.yaml"




def test_default_root_is_repo_root(lint_module):
    """With no ROOT argument the repository root is used, from any cwd."""
    assert os.path.realpath(lint_module.default_root()) == \
        os.path.realpath(str(REPO_ROOT))


def test_module_main_unique_ids_pass(lint_module, tmp_path, capsys):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.a"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.b"))
    assert lint_module.main([str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "OK: 2 test suite ids in 2 file(s) are unique" in out


def test_module_main_duplicate_id_fails(lint_module, tmp_path, capsys):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.dup"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.dup"))
    assert lint_module.main([str(tmp_path)]) == 1
    err = capsys.readouterr().err
    assert ("ERROR: duplicate test suite id 'sample.wolfssl.dup' in "
            "app-a/sample.yaml and app-b/sample.yaml" in err)


def test_module_main_json_summary(lint_module, tmp_path, capsys):
    make_app(tmp_path, "app-a", sample_yaml(name="App A", sid="sample.wolfssl.a",
                                            allow=[GOOD_BOARD]))
    assert lint_module.main(["--json", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    summary = json.loads(out)
    assert summary["status"] == "ok"
    assert summary["files"] == 1
    assert summary["platform_check"] == "skipped"
    assert summary["errors"] == []
    entry = summary["tests"][0]
    assert entry["id"] == "sample.wolfssl.a"
    assert entry["file"] == "app-a/sample.yaml"
    assert entry["name"] == "App A"
    assert entry["platform_allow"] == [GOOD_BOARD]




def test_unique_ids_pass_cli(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.a"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.b"))
    result = run_cli(str(tmp_path), cwd="/")  # must work from any cwd
    assert result.returncode == 0, result.stderr
    assert "OK: 2 test suite ids in 2 file(s) are unique" in result.stdout
    assert "ERROR" not in result.stdout + result.stderr


def test_duplicate_id_cli_names_both_files(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.dup"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.dup"))
    result = run_cli(str(tmp_path))
    assert result.returncode == 1
    line = ("ERROR: duplicate test suite id 'sample.wolfssl.dup' in "
            "app-a/sample.yaml and app-b/sample.yaml")
    assert line in result.stderr.splitlines()
    assert "ERROR" not in result.stdout


def test_duplicate_name_cli_case_insensitive_and_stripped(tmp_path):
    # Name comparison must ignore case and surrounding spaces.
    make_app(tmp_path, "app-a",
             sample_yaml(name="TLS 1.3 Hello Server",
                         sid="sample.wolfssl.tls_hello_server"))
    make_app(tmp_path, "app-b",
             sample_yaml(name='"  tls 1.3 hello server  "',
                         sid="sample.wolfssl.tls_hello_server_pqc"))
    result = run_cli(str(tmp_path))
    assert result.returncode == 1
    assert ("ERROR: duplicate test name 'TLS 1.3 Hello Server' in "
            "app-a/sample.yaml and app-b/sample.yaml" in result.stderr)


@pytest.mark.parametrize(
    "bad_id",
    ["Sample.wolfssl.app",   # capital letter
     "_private",             # underscore-only (and leading underscore)
     "sample"],              # no dot (one component only)
)
def test_bad_suite_id_cli(tmp_path, bad_id):
    make_app(tmp_path, "app-a", sample_yaml(sid=bad_id))
    result = run_cli(str(tmp_path))
    assert result.returncode == 1
    assert ("ERROR: test suite id '%s' in app-a/sample.yaml is not a "
            "lower-case dotted id (expected e.g. sample.wolfssl.tls_hello_server)"
            % bad_id in result.stderr)


def test_missing_tests_section_cli(tmp_path):
    make_app(tmp_path, "app-a", "sample:\n  name: Only A Name\n")
    result = run_cli(str(tmp_path))
    assert result.returncode == 1
    assert "declares no test suite" in result.stderr


def test_malformed_yaml_cli_exit_1(tmp_path):
    # Malformed YAML is a lint error (exit 1; 2 is for environment errors).
    make_app(tmp_path, "app-a", "tests:\n  sample.wolfssl.a:\n    tags: [unclosed\n")
    result = run_cli(str(tmp_path))
    assert result.returncode == 1
    assert result.stderr.startswith("ERROR: cannot parse app-a/sample.yaml:")


def test_platform_check_catches_bogus_id(tmp_path):
    make_app(tmp_path, "app-a",
             sample_yaml(sid="sample.wolfssl.a",
                         allow=[GOOD_BOARD, "bogus_board_xyz"]))
    platforms = tmp_path / "platforms.json"
    platforms.write_text(json.dumps([GOOD_BOARD, GOOD_BOARD_NS]))
    result = run_cli(str(tmp_path), "--platforms", str(platforms))
    assert result.returncode == 1
    assert ("ERROR: platform 'bogus_board_xyz' in app-a/sample.yaml is not "
            "a known Zephyr platform (platform metadata mismatch)"
            in result.stderr)


def test_platform_check_accepts_good_ids(tmp_path):
    make_app(tmp_path, "app-a",
             sample_yaml(sid="sample.wolfssl.a",
                         allow=[GOOD_BOARD], integration=[GOOD_BOARD_NS]))
    platforms = tmp_path / "platforms.json"
    platforms.write_text(json.dumps([GOOD_BOARD, GOOD_BOARD_NS]))
    result = run_cli(str(tmp_path), "--platforms", str(platforms))
    assert result.returncode == 0, result.stderr
    assert "NOT found" not in result.stdout  # no skip NOTE when checked
    assert "OK: 1 test suite ids in 1 file(s) are unique" in result.stdout


def test_platform_allow_as_single_string_and_text_platform_file(tmp_path):
    # platform_allow as a space-separated string, with a plain-text platforms file.
    make_app(tmp_path, "app-a",
             sample_yaml(sid="sample.wolfssl.a",
                         allow_raw='"%s %s"' % (GOOD_BOARD, GOOD_BOARD_NS)))
    platforms = tmp_path / "platforms.txt"
    platforms.write_text("# known boards\n%s\n%s\n" % (GOOD_BOARD,
                                                       GOOD_BOARD_NS))
    result = run_cli(str(tmp_path), "--platforms", str(platforms))
    assert result.returncode == 0, result.stderr

    # The very same string form must fail when the metadata lacks one id.
    platforms.write_text(GOOD_BOARD + "\n")
    result = run_cli(str(tmp_path), "--platforms", str(platforms))
    assert result.returncode == 1
    assert ("ERROR: platform '%s' in app-a/sample.yaml is not a known "
            "Zephyr platform (platform metadata mismatch)" % GOOD_BOARD_NS
            in result.stderr)


def test_json_output_is_valid_json(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.a"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.b"))
    result = run_cli(str(tmp_path), "--json")
    assert result.returncode == 0, result.stderr
    summary = json.loads(result.stdout)  # stdout must be pure JSON
    assert summary["status"] == "ok"
    assert summary["files"] == 2
    assert len(summary["tests"]) == 2
    assert {t["id"] for t in summary["tests"]} == {
        "sample.wolfssl.a", "sample.wolfssl.b"}
    for entry in summary["tests"]:
        assert set(entry) == {"file", "id", "name", "platform_allow",
                              "integration_platforms"}


def test_json_output_on_failure(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.dup"))
    make_app(tmp_path, "app-b", sample_yaml(sid="sample.wolfssl.dup"))
    result = run_cli(str(tmp_path), "--json")
    assert result.returncode == 1
    summary = json.loads(result.stdout)
    assert summary["status"] == "fail"
    assert any("duplicate test suite id" in e for e in summary["errors"])


def test_build_output_copies_are_not_double_counted(tmp_path):
    # Copies under twister-out*, zephyr/ and modules/ must be skipped.
    content = sample_yaml(sid="sample.wolfssl.a", allow=[GOOD_BOARD])
    make_app(tmp_path, "app-a", content)
    for skip_dir in ("twister-out", "twister-out-frdm_mcxn947_mcxn947_cpu0",
                     "modules", "zephyr", "node_modules", ".west", "__repo__",
                     ".git"):
        make_app(tmp_path, os.path.join(skip_dir, "app-a"), content)
    result = run_cli(str(tmp_path))
    assert result.returncode == 0, result.stderr
    assert "OK: 1 test suite ids in 1 file(s) are unique" in result.stdout


def test_note_when_platform_check_skipped(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.a"))
    result = run_cli(str(tmp_path))  # no --platforms file
    assert result.returncode == 0, result.stderr
    assert ("NOTE: platform metadata not found, skipped platform check"
            in result.stdout)


def test_verbose_lists_every_id(tmp_path):
    make_app(tmp_path, "app-a", sample_yaml(sid="sample.wolfssl.a"))
    result = run_cli(str(tmp_path), "--verbose")
    assert result.returncode == 0, result.stderr
    assert "app-a/sample.yaml: sample.wolfssl.a" in result.stdout


def test_missing_pyyaml_exits_2():
    # Simulate an interpreter without PyYAML.
    code = (
        "import sys\n"
        "sys.modules['yaml'] = None\n"
        "sys.argv = ['check_twister_ids.py', '.']\n"
        "exec(compile(open(%r, 'r', encoding='utf-8').read(), %r, 'exec'),"
        " {'__name__': '__main__'})\n"
    ) % (str(SCRIPT), str(SCRIPT))
    result = subprocess.run([sys.executable, "-c", code],
                            capture_output=True, text=True, cwd="/")
    assert result.returncode == 2
    assert "ERROR: PyYAML is required (pip install pyyaml)" in result.stderr
