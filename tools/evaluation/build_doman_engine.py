"""Build/test a pinned offline extension in a fresh directory (Linux/WSL)."""
import argparse
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile

from doman_engine import BASE_COMMIT, PATCH, REVISION, sha256


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mortal-source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != "linux":
        parser.error("Build under Linux/WSL with the Python used for evaluation")
    output = args.output.resolve()
    if output.exists():
        parser.error("Output exists; choose a fresh directory")
    source = args.mortal_source.resolve()
    archive = subprocess.check_output(["git", "-C", str(source), "archive", BASE_COMMIT])
    output.mkdir(parents=True)
    checkout = output / "source"
    checkout.mkdir()
    manifest = dict(status="building", base_commit=BASE_COMMIT, rules_revision=REVISION,
                    patch_sha256=sha256(PATCH), python=sys.version,
                    rustc=subprocess.check_output(["rustc", "--version"], text=True).strip())
    path = output / "build-manifest.json"
    def save():
        path.write_text(json.dumps(manifest, indent=2)+"\n", encoding="utf-8")
    save()
    try:
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            tar.extractall(checkout, filter="data")
        subprocess.run(["git", "init", "--quiet"], cwd=checkout, check=True)
        subprocess.run(["git", "apply", "--check", str(PATCH)], cwd=checkout, check=True)
        subprocess.run(["git", "apply", str(PATCH)], cwd=checkout, check=True)
        env = {**os.environ, "PYO3_PYTHON": sys.executable}
        for label, command in (
            ("tests", ["cargo", "test", "--locked", "-p", "libriichi", "--lib", "--no-default-features"]),
            ("build", ["cargo", "build", "--release", "--locked", "-p", "libriichi", "--lib"]),
        ):
            print(f"Running {label}; log: {output / (label+'.log')}", flush=True)
            with (output / (label+".log")).open("w", encoding="utf-8") as log:
                subprocess.run(command, cwd=checkout, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        library = output / "libriichi_doman.so"
        shutil.copy2(checkout / "target/release/libriichi_doman.so", library)
        # Import the actual product in a clean interpreter before admitting the build.
        check = ("import sys; sys.path.insert(0, sys.argv[1]); import libriichi_doman as m; "
                 f"assert m.DOMAN_RULES_REVISION == {REVISION}; "
                 f"assert m.DOMAN_BASE_COMMIT == '{BASE_COMMIT}'; "
                 "m.arena.OneVsThree(match_mode='doman-quick'); m.arena.OneVsThree(match_mode='doman-full')")
        subprocess.run([sys.executable, "-c", check, str(output)], cwd=output, check=True)
        manifest.update(status="validated", library_sha256=sha256(library),
                        tests_log_sha256=sha256(output/"tests.log"), build_log_sha256=sha256(output/"build.log"))
        save()
        print(f"Validated engine: {output}")
    except BaseException as error:
        manifest.update(status="failed", error=f"{type(error).__name__}: {error}")
        save()
        raise


if __name__ == "__main__":
    main()
