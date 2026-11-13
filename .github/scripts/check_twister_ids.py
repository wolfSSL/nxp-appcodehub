#!/usr/bin/env python3
#
# check_twister_ids.py
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

# Lint sample.yaml/testcase.yaml suite ids before Twister (exit 0 ok, 1 lint error, 2 env error).

import argparse
import json
import os
import re
import sys

try:
    import yaml
except ImportError:
    print("ERROR: PyYAML is required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)

# Build output and west-managed trees hold copies of the apps, so never walk them.
SKIP_DIR_EXACT = {".git", "zephyr", "modules", ".west", "node_modules", "__repo__"}
SKIP_DIR_PREFIX = ("twister-out",)

# File names that define Twister test suites.
YAML_TEST_FILES = ("sample.yaml", "testcase.yaml")

# Check C: lower-case, dot-separated id with at least two components.
SUITE_ID_RE = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$")

EXIT_OK = 0
EXIT_LINT = 1
EXIT_ENV = 2

# NOTE printed on success when check D is skipped (no --platforms file).
NOTE_PLATFORM_SKIPPED = "NOTE: platform metadata not found, skipped platform check"


def default_root():
    """Repository root: two levels above this .github/scripts/ file."""
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def rel_posix(path, root):
    """Path relative to ROOT with '/' separators (deterministic messages)."""
    return os.path.relpath(path, root).replace(os.sep, "/")


def is_skipped_dir(name):
    """True for build output, west-managed trees and VCS/workspace dirs."""
    return name in SKIP_DIR_EXACT or name.startswith(SKIP_DIR_PREFIX)


def find_test_files(root):
    """All sample.yaml / testcase.yaml under ROOT, sorted, skipping build output."""
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        # Prune in place so os.walk never descends into build or west trees.
        dirnames[:] = sorted(d for d in dirnames if not is_skipped_dir(d))
        for filename in filenames:
            if filename in YAML_TEST_FILES:
                found.append(os.path.join(dirpath, filename))
    found.sort(key=lambda p: rel_posix(p, root))
    return found


def parse_yaml_file(path, rel):
    """Parse one YAML file. Returns (doc, None) or (None, error_message)."""
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return yaml.safe_load(fh), None
    except yaml.YAMLError as exc:
        reason = " ".join(str(exc).split())  # keep the message on one line
        return None, "ERROR: cannot parse %s: %s" % (rel, reason)
    except OSError as exc:
        reason = exc.strerror or str(exc)
        return None, "ERROR: cannot parse %s: %s" % (rel, reason)


def platform_list(value):
    """Normalize platform_allow (a list or a space-separated string) to a list."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return [str(v).strip() for v in value
                if v is not None and str(v).strip()]
    if isinstance(value, str):
        return [part.strip() for part in value.split() if part.strip()]
    return [str(value)]


def load_platforms_file(path):
    """Read known platform ids from JSON or plain text; return (ids, None) or (None, error)."""
    try:
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
    except OSError as exc:
        return None, "ERROR: cannot read platform file %s: %s" % (
            path, exc.strerror or str(exc))
    data = None
    try:
        data = json.loads(text)
    except ValueError:
        data = None
    if data is None:
        # Plain text: one id per line, blank lines and '#' comments skipped.
        ids = [line.strip() for line in text.splitlines()]
        return [line for line in ids if line and not line.startswith("#")], None
    if isinstance(data, list):
        return [str(x) for x in data if x is not None and str(x).strip()], None
    if isinstance(data, dict):
        for key in ("platforms", "boards", "board"):
            value = data.get(key)
            if isinstance(value, list):
                return [str(x) for x in value
                        if x is not None and str(x).strip()], None
        for value in data.values():  # last resort: first list found
            if isinstance(value, list):
                return [str(x) for x in value
                        if x is not None and str(x).strip()], None
    return None, "ERROR: could not read any platform ids from %s" % path


def iter_suites(doc):
    """Yield (suite_id, attributes) from every sample.yaml shape Twister accepts."""
    out = []
    testsuite = doc.get("testsuite")
    testsuites = doc.get("testsuites")
    if (isinstance(testsuite, dict) and testsuite.get("name")) or \
            isinstance(testsuites, list):
        # testsuite:/testsuites: form, where tests: lists test cases, not suite ids.
        if isinstance(testsuite, dict) and testsuite.get("name"):
            out.append((str(testsuite["name"]), testsuite))
        if isinstance(testsuites, list):
            for entry in testsuites:
                if isinstance(entry, dict) and entry.get("name"):
                    out.append((str(entry["name"]), entry))
        return out
    tests = doc.get("tests")
    if isinstance(tests, dict):
        for suite_id, entry in tests.items():
            out.append((str(suite_id), entry if isinstance(entry, dict) else {}))
    elif isinstance(tests, list):
        for entry in tests:
            if isinstance(entry, dict) and entry.get("name"):
                out.append((str(entry["name"]), entry))
    return out


def join_and(paths):
    """'a' for one path, 'a and b' for two, 'a, b and c' for three."""
    if len(paths) == 1:
        return paths[0]
    return ", ".join(paths[:-1]) + " and " + paths[-1]


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="check_twister_ids.py",
        description="Lint Zephyr Twister test definitions (sample.yaml / "
                    "testcase.yaml) for duplicate or malformed test suite "
                    "ids before any build (wolfSSL/nxp-appcodehub issue "
                    "#22).")
    parser.add_argument(
        "root", nargs="?", default=None,
        help="repository root to lint (default: the repository root that "
             "contains this .github/scripts/ directory)")
    parser.add_argument(
        "--platforms", metavar="FILE", default=None,
        help="known Zephyr platform ids (JSON list or one id per line); "
             "enables check D, which is skipped with a NOTE otherwise")
    parser.add_argument(
        "--verbose", action="store_true",
        help="list every suite id with its file")
    parser.add_argument(
        "--json", action="store_true",
        help="print a machine-readable summary object to stdout instead of "
             "the human-readable report")
    args = parser.parse_args(argv)

    root = os.path.abspath(args.root) if args.root else default_root()
    if not os.path.isdir(root):
        print("ERROR: root directory %s does not exist" % (args.root,),
              file=sys.stderr)
        return EXIT_ENV

    files = find_test_files(root)
    if not files:
        print("ERROR: no %s file(s) found under %s"
              % ("/".join(YAML_TEST_FILES), args.root), file=sys.stderr)
        return EXIT_LINT

    errors = []          # all lint errors, in fixed deterministic order
    tests = []           # (rel, id, name, platform_allow, integration_platforms)
    id_owners = {}       # suite id -> [rel, ...]   (check A)
    name_owners = {}     # normalized name -> [rel, ...]  (check B)
    name_display = {}    # normalized name -> original trimmed name

    for path in files:
        rel = rel_posix(path, root)
        doc, err = parse_yaml_file(path, rel)
        if err is not None:
            errors.append(err)
            continue
        if not isinstance(doc, dict):
            errors.append("ERROR: %s is not a mapping" % rel)
            continue
        suites = iter_suites(doc)
        if not suites:
            errors.append("ERROR: %s declares no test suite (no 'tests:', "
                          "'testsuite:' or 'testsuites:' section)" % rel)
            continue

        sample = doc.get("sample")
        name = sample.get("name") if isinstance(sample, dict) else None
        if name is not None and str(name).strip():
            key = str(name).strip().casefold()
            name_owners.setdefault(key, []).append(rel)
            name_display.setdefault(key, str(name).strip())
        name = str(name).strip() if name is not None else None

        for suite_id, entry in suites:
            allow = platform_list(entry.get("platform_allow"))
            integration = platform_list(entry.get("integration_platforms"))

            tests.append((rel, suite_id, name, allow, integration))
            id_owners.setdefault(suite_id, []).append(rel)

            # Check C: lower-case dotted id.
            if not SUITE_ID_RE.match(suite_id):
                errors.append(
                    "ERROR: test suite id '%s' in %s is not a lower-case "
                    "dotted id (expected e.g. sample.wolfssl.tls_hello_server)"
                    % (suite_id, rel))

    # Check A: one suite id must not appear in two different files.
    for suite_id in sorted(id_owners):
        owners = id_owners[suite_id]
        if len(owners) > 1:
            errors.append("ERROR: duplicate test suite id '%s' in %s"
                          % (suite_id, join_and(owners)))

    # Check B: the human-readable name must not be shared by two files.
    for key in sorted(name_owners):
        owners = name_owners[key]
        if len(owners) > 1:
            errors.append("ERROR: duplicate test name '%s' in %s"
                          % (name_display[key], join_and(owners)))

    # Check D: platform ids must be known (only when metadata was given).
    platform_known = None
    if args.platforms:
        platforms, err = load_platforms_file(args.platforms)
        if err is not None:
            print(err, file=sys.stderr)
            return EXIT_ENV
        if not platforms:
            print("ERROR: no platform ids could be read from %s"
                  % args.platforms, file=sys.stderr)
            return EXIT_ENV
        platform_known = set(platforms)
        for rel, suite_id, _name, allow, integration in tests:
            for platform in sorted(set(allow + integration)):
                if platform not in platform_known:
                    errors.append(
                        "ERROR: platform '%s' in %s is not a known Zephyr "
                        "platform (platform metadata mismatch)"
                        % (platform, rel))

    if args.json:
        # stdout carries only JSON; errors also go to stderr.
        summary = {
            "status": "ok" if not errors else "fail",
            "files": len(files),
            "platform_check": "checked" if platform_known is not None
                              else "skipped",
            "errors": errors,
            "tests": [
                {"file": rel, "id": suite_id, "name": name,
                 "platform_allow": allow,
                 "integration_platforms": integration}
                for rel, suite_id, name, allow, integration in tests
            ],
        }
        if errors:
            for line in errors:
                print(line, file=sys.stderr)
        print(json.dumps(summary, indent=2, sort_keys=True))
        return EXIT_OK if not errors else EXIT_LINT

    if args.verbose:
        for rel, suite_id, _name, _allow, _integration in tests:
            print("  %s: %s" % (rel, suite_id))
    if errors:
        for line in errors:
            print(line, file=sys.stderr)
        return EXIT_LINT
    if platform_known is None:
        print(NOTE_PLATFORM_SKIPPED)
    print("OK: %d test suite ids in %d file(s) are unique"
          % (len(tests), len(files)))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
