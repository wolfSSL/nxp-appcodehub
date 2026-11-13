#!/usr/bin/env python3
# Gate on Twister JSON reports: fail if nothing was built or anything failed or warned.

import argparse
import glob
import json
import os
import sys

BAD_STATUSES = ("error", "failed", "warning")
BUILT_STATUSES = ("not run", "passed", "passed (legacy)")


def load_reports(paths, kind="twister"):
    if not paths:
        name = "twister.json" if kind == "twister" else "testplan.json"
        paths = sorted(glob.glob(f"twister-out*/**/{name}", recursive=True))
        paths += sorted(glob.glob(f"twister-out*/{name}"))
        paths = sorted(set(os.path.abspath(p) for p in paths))
    return [p for p in paths if os.path.isfile(p)]


def collect(report_files):
    rows = []
    environment = {}
    for path in report_files:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        environment.update(data.get("environment", {}) or {})
        for suite in data.get("testsuites", []) or []:
            rows.append({
                "report": os.path.relpath(path, os.getcwd()),
                "name": suite.get("name", "?"),
                "platform": suite.get("platform", "?"),
                "toolchain": suite.get("toolchain", "?"),
                "status": (suite.get("status") or "?").lower(),
                "reason": suite.get("reason") or "",
                "build_time": suite.get("build_time", ""),
            })
    return rows, environment


def collect_testplan(report_files):
    """Check a --dry-run testplan.json: fail when an app is missing from the plan."""
    rows = []
    environment = {}
    for path in report_files:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        environment.update(data.get("environment", {}) or {})
        for suite in data.get("testsuites", []) or []:
            for inst in suite.get("tests", []) or [{}]:
                rows.append({
                    "report": os.path.relpath(path, os.getcwd()),
                    "name": suite.get("name", "?"),
                    "platform": inst.get("platform", suite.get("platform", "?")),
                    "toolchain": inst.get("toolchain", "?"),
                    "status": "planned",
                    "reason": "",
                    "build_time": "",
                })
    return rows, environment


def main():
    ap = argparse.ArgumentParser(
        description="Gate and summarise Twister JSON reports")
    ap.add_argument("reports", nargs="*", help="twister.json files")
    ap.add_argument("--github-summary", action="store_true",
                    help="append a markdown table to $GITHUB_STEP_SUMMARY")
    ap.add_argument("--allow-warnings", action="store_true",
                    help="do not fail on the 'warning' status")
    ap.add_argument("--min-built", type=int, default=1,
                    help="fail if fewer than N suites were built (default 1)")
    ap.add_argument("--testplan", action="store_true",
                    help="read Twister --dry-run testplan.json (no statuses), "
                         "i.e. check what the plan covers instead of what built")
    args = ap.parse_args()

    report_files = load_reports(args.reports, "testplan" if args.testplan else "twister")
    if not report_files:
        print("ERROR: no {} report found (pass one, or run from a directory "
              "containing twister-out*/)".format(
                  "testplan.json" if args.testplan else "twister.json"),
              file=sys.stderr)
        return 2

    if args.testplan:
        rows, env = collect_testplan(report_files)
    else:
        rows, env = collect(report_files)
    counts = {}
    for row in rows:
        counts[row["status"]] = counts.get(row["status"], 0) + 1

    bad = [r for r in rows if r["status"] in BAD_STATUSES
           and not (r["status"] == "warning" and args.allow_warnings)]
    built = [r for r in rows
             if args.testplan or r["status"] in BUILT_STATUSES]

    print("Twister {} reports: {}".format(
        "dry-run" if args.testplan else "",
        ", ".join(os.path.basename(r) for r in report_files)).replace("Twister  ", "Twister "))
    if env:
        print("  zephyr {} toolchain {}".format(env.get("zephyr_version", "?"),
                                                env.get("toolchain", "?")))
    print("  suites: {}  built: {}  {}".format(
        len(rows), len(built),
        "  ".join("{}={}".format(k, v) for k, v in sorted(counts.items()))))
    for row in bad:
        print("ERROR: {} on {} -> {} {}".format(
            row["name"], row["platform"], row["status"], row["reason"]),
            file=sys.stderr)

    if args.github_summary and os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
            fh.write("| app | board | status | build s | reason |\n")
            fh.write("|---|---|---|---|---|\n")
            for row in sorted(rows, key=lambda r: (r["platform"], r["name"])):
                fh.write("| `{}` | `{}` | {} | {} | {} |\n".format(
                    row["name"], row["platform"], row["status"],
                    row["build_time"], row["reason"].replace("\n", " ")[:200]))
            fh.write("\nTotals: {}\n".format(
                ", ".join("{} = {}".format(k, v) for k, v in sorted(counts.items()))))

    if bad:
        print("FAILED: {} suite(s) in error/failed/warning state".format(len(bad)),
              file=sys.stderr)
        return 1
    if len(built) < args.min_built:
        print("FAILED: only {} suite(s) {}, expected at least {}".format(
            len(built), "planned" if args.testplan else "built", args.min_built),
            file=sys.stderr)
        return 1
    print("OK: {} suite(s) {}, 0 errors, 0 warnings".format(
        len(built), "planned" if args.testplan else "built"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
