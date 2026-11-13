#!/usr/bin/env python3
"""Build every app headless on Linux via setup.sh and MCUXpresso for VS Code's Import and Build tasks."""
import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
EXTENSION = "NXPSemiconductors.mcuxpresso"


def vscode_download(dest: Path):
    """Fetch a portable Linux VS Code into dest once; return (CLI, app executable)."""
    if platform.system() != "Linux":
        sys.exit("this driver runs on Linux only")
    arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
    top = f"VSCode-linux-{arch}"
    code, exe = dest / top / "bin" / "code", dest / top / "code"
    if code.exists():
        return code, exe
    dest.mkdir(parents=True, exist_ok=True)
    archive = dest / "vscode.tar.gz"
    urllib.request.urlretrieve(f"https://update.code.visualstudio.com/latest/linux-{arch}/stable", archive)
    with tarfile.open(archive) as t:
        t.extractall(dest)
    archive.unlink()
    return code, exe


def stop(proc) -> None:
    """End VS Code and everything it started (extension host, builds)."""
    if proc.poll() is not None:
        return
    try:
        os.killpg(proc.pid, 15)
        proc.wait(timeout=20)
    except (ProcessLookupError, subprocess.TimeoutExpired):
        os.killpg(proc.pid, 9)


def code_cmd(code: Path, profile: Path, extensions: Path) -> list:
    return [str(code), "--no-sandbox", "--user-data-dir", str(profile), "--extensions-dir", str(extensions)]


def write_settings(profile: Path, venv: Path) -> None:
    west = venv / "bin" / "west"
    (profile / "User").mkdir(parents=True, exist_ok=True)
    (profile / "User" / "settings.json").write_text(json.dumps({
        "mcuxpresso.python.west.path": str(west),
        "mcuxpresso.python.virtualEnvironment": "custom",
        "mcuxpresso.python.virtualEnvironmentPath": str(venv),
        "security.workspace.trust.enabled": False,
        "telemetry.telemetryLevel": "off",
        "update.mode": "none",
        "extensions.autoUpdate": False,
    }, indent=2))


def task(label, out, **options):
    return {"type": "MCUXpresso", "version": "1.0", "label": label, "commandOptions": options,
            "runOptions": {"taskOutputFile": str(out / f"{label}.log")}}


def run_session(name, tasks, apps, args, code, extensions) -> list:
    """Run `tasks` in one VS Code launch with a fresh profile; return the per-task results."""
    out = args.out / name
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)
    # Short profile path: VS Code's IPC socket lives in it and socket paths are length-capped.
    profile = Path(tempfile.mkdtemp(prefix="mcux-profile-", dir="/tmp"))
    write_settings(profile, args.venv)
    tasks_file = out / "mcuxpresso-tasks.json"
    tasks_file.write_text(json.dumps({"version": "1.0.0", "exitVSCodeOnCompletion": True,
                                      "tasksCompletionTimeout": args.timeout,
                                      "tasks": [t(out) for t in tasks]}, indent=2))
    # Import does not add the app to a headless window, so open them up front.
    workspace = args.out / f"{name}.code-workspace"
    workspace.write_text(json.dumps({"folders": [{"path": str(out)}] +
                                     [{"path": str(REPO / a)} for a in apps]}))
    cmd = code_cmd(code, profile, extensions) + ["--disable-gpu", "--new-window", str(workspace)]
    if shutil.which("xvfb-run"):
        cmd = ["xvfb-run", "-a"] + (["dbus-run-session", "--"] if shutil.which("dbus-run-session") else []) + cmd
    env = dict(os.environ, MCUXPRESSO_TASKS_FILE=str(tasks_file))
    print(f"== {name}: {len(tasks)} tasks", flush=True)
    # Done when the extension writes its result file, then VS Code is stopped.
    result = out / "mcuxpresso-tasks.result.json"
    deadline = time.monotonic() + args.timeout + 600
    with open(out / "code.log", "w") as log:
        proc = subprocess.Popen(cmd, env=env, stdout=log, stderr=subprocess.STDOUT,
                                start_new_session=True)
        while not result.exists() and proc.poll() is None and time.monotonic() < deadline:
            time.sleep(5)
        if not result.exists() and proc.poll() is None:
            print(f"::error::{name}: VS Code did not finish in time")
        time.sleep(5)  # let the extension finish writing the result
        stop(proc)
    shutil.copytree(profile / "logs", out / "vscode-logs", dirs_exist_ok=True)
    shutil.rmtree(profile, ignore_errors=True)
    if not result.exists():
        print(f"::error::{name}: the extension wrote no {result.name} (see {out / 'code.log'})")
        return [{"label": t(out)["label"], "executionResult": "NO_RESULT"} for t in tasks]
    return json.loads(result.read_text())["tasksResults"]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--west-workspace", type=Path, required=True, help="west topdir (holds zephyr/)")
    ap.add_argument("--sdk", type=Path, required=True, help="Zephyr SDK directory")
    ap.add_argument("--venv", type=Path, required=True, help="Python venv with west and Zephyr's requirements")
    ap.add_argument("--out", type=Path, default=REPO / "mcuxpresso-out", help="work and results directory")
    ap.add_argument("--vscode-dir", type=Path, help="portable VS Code location (default: <out>/vscode)")
    ap.add_argument("--extension", default=EXTENSION, help="extension id, optionally id@version")
    ap.add_argument("--configs", nargs="+", default=["debug", "release"])
    ap.add_argument("--apps", nargs="*", help="app folders (default: every dm-* app)")
    ap.add_argument("--timeout", type=int, default=5400, help="per-session task timeout, seconds")
    args = ap.parse_args()
    args.out = args.out.resolve()
    apps = args.apps or sorted(p.name for p in REPO.glob("dm-*") if (p / "CMakeLists.txt").exists())

    code, vscode_app = vscode_download((args.vscode_dir or args.out / "vscode").resolve())
    extensions = args.out / "extensions"
    base = code_cmd(code, args.out / "profile-install", extensions)
    subprocess.run(base + ["--install-extension", args.extension, "--force"], check=True)
    versions = subprocess.run(base + ["--list-extensions", "--show-versions"], check=True,
                              capture_output=True, text=True).stdout
    print("VS Code", subprocess.run(base + ["--version"], capture_output=True, text=True).stdout.split()[0])
    print(versions)

    # The README's setup step.
    for app in apps:
        subprocess.run(["bash", "./setup.sh", app], cwd=REPO, input="y\n", text=True, check=True)

    west = str(args.west_workspace.resolve())
    tasks = [lambda o: task("repo", o, command="installLocalRepo", repoPath=west)]
    tasks += [lambda o, a=a: task(f"import-{a}", o, command="importProject",
                                  projectPath=str(REPO / a), projectDestinationPath=str(REPO),
                                  toolchainPath=str(args.sdk.resolve()), repoPath=west)
              for a in apps]
    for i, config in enumerate(args.configs):
        for a in apps:
            # Extension 26.9 builds before switching configs, so select it with a throwaway build first.
            if i > 0:
                tasks.append(lambda o, a=a, c=config: task(f"select-{a}-{c}", o, command="buildProject",
                                                           projectName=a, buildConfigName=c))
            tasks.append(lambda o, a=a, c=config: task(f"build-{a}-{c}", o, command="buildProject",
                                                       projectName=a, buildConfigName=c))
    results = run_session("session", tasks, apps, args, vscode_app, extensions)

    # A task can pass without producing images, so check the outputs too.
    results = [r for r in results if not r["label"].startswith("select-")]
    failed = [r for r in results if r.get("executionResult") != "TASK_SUCCEEDED"]
    missing = []
    for app in apps:
        for config in args.configs:
            images = ["zephyr/zephyr.elf"] + (["zephyr/factory.bin"] if (REPO / app / "wolfbootConfig").is_dir() else [])
            missing += [f"{app}/{config}/{i}" for i in images if not (REPO / app / config / i).exists()]

    lines = ["| Task | Result | Seconds |", "|---|---|---|"]
    lines += [f"| {r['label']} | {r.get('executionResult')} | {r.get('duration', 0):.0f} |" for r in results]
    lines += [f"\nMissing build output: `{m}`" for m in missing]
    summary = "\n".join(lines)
    print(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as f:
            f.write(f"## MCUXpresso for VS Code\n\n{versions}\n\n{summary}\n")
    for r in failed:
        print(f"::error::{r['label']}: {r.get('executionResult')} {r.get('executionError', '')}")
    return 1 if failed or missing else 0


if __name__ == "__main__":
    sys.exit(main())
