"""Load an isolated, verified offline extension without replacing online libriichi."""
import hashlib
import importlib.util
import json
from pathlib import Path

BASE_COMMIT = "0cff2b52982be5b1163aa9a62fb01f03ce91e0d2"
REVISION = 1
PATCH = Path(__file__).with_name("patches") / "libriichi-doman.patch"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify_build(directory):
    directory = Path(directory).resolve()
    manifest = json.loads((directory / "build-manifest.json").read_text(encoding="utf-8"))
    library = directory / "libriichi_doman.so"
    expected = dict(status="validated", base_commit=BASE_COMMIT, rules_revision=REVISION,
                    patch_sha256=sha256(PATCH), library_sha256=sha256(library))
    if any(manifest.get(key) != value for key, value in expected.items()):
        raise ValueError("Doman build is stale, untested or altered; rebuild it before evaluation")
    return library, manifest


def load_doman(directory):
    if directory is None:
        raise ValueError("Doman simulation requires --doman-engine; build it with build_doman_engine.py")
    library, manifest = verify_build(directory)
    spec = importlib.util.spec_from_file_location("libriichi_doman", library)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if module.DOMAN_RULES_REVISION != REVISION or module.DOMAN_BASE_COMMIT != BASE_COMMIT:
        raise ValueError("Doman native capabilities do not match the declared build")
    return module.arena.OneVsThree, module.dataset.Grp, manifest


def validate_match_events(events, mode):
    if mode == "libriichi-hanchan":
        return
    allowed = {"E"} if mode == "doman-quick" else {"E", "S"}
    starts = [event for event in events if event["type"] == "start_kyoku"]
    if not starts or starts[0].get("bakaze") != "E" or starts[0].get("kyoku") != 1:
        raise ValueError("Doman log must start at East 1")
    for event in starts:
        if event.get("bakaze") not in allowed or event.get("kyoku") not in (1, 2, 3, 4):
            raise ValueError("Log exceeds the requested Doman match length")
