"""Build the bounded feedback entry point in a separate Doman library."""
import argparse
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile

from doman_engine import BASE_COMMIT, PATCH, DATASET_PATCH, sha256


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args(); repo=args.repo.resolve(); output=args.output.resolve()
    output.mkdir(parents=True,exist_ok=False); source=output/'source'; source.mkdir()
    native=Path(__file__).with_name('feedback_probe.rs')
    manifest=dict(status='building',base_commit=BASE_COMMIT,rules_patch_sha256=sha256(PATCH),
                  dataset_patch_sha256=sha256(DATASET_PATCH),probe_sha256=sha256(native),
                  builder_sha256=sha256(__file__),online_library_modified=False)
    def save(): (output/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    save()
    try:
        archive=subprocess.check_output(['git','-C',str(repo/'.loacl/Mortal'),'archive',BASE_COMMIT])
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar: tar.extractall(source,filter='data')
        subprocess.run(['git','init','--quiet'],cwd=source,check=True)
        for patch in (PATCH,DATASET_PATCH):
            subprocess.run(['git','apply',str(patch)],cwd=source,check=True)
        arena=source/'libriichi/src/arena'
        shutil.copy2(native,arena/'feedback_probe.rs')
        game=arena/'game.rs'; game.write_text(game.read_text()+'\ninclude!("feedback_probe.rs");\n')
        module=arena/'mod.rs'; text=module.read_text()
        marker='    m.add_class::<OneVsThree>()?;'
        assert text.count(marker)==1
        module.write_text(text.replace(marker,marker+'\n    m.add_function(wrap_pyfunction!(game::probe_branches, &m)?)?;'))
        # Only build cache is shared; the previously verified exported .so stays intact.
        target=repo/'artifacts/doman-engine-v2-dataset/source/target'
        env={**os.environ,'PYO3_PYTHON':sys.executable,'CARGO_TARGET_DIR':str(target)}
        for label,command in [
            ('tests',['cargo','test','--locked','--offline','-p','libriichi','--lib','--no-default-features','arena::']),
            ('build',['cargo','build','--release','--locked','--offline','-p','libriichi','--lib'])]:
            print('Running '+label,flush=True)
            with (output/(label+'.log')).open('w') as log:
                subprocess.run(command,cwd=source,env=env,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=1200)
        library=output/'libriichi_doman.so'
        shutil.copy2(target/'release/libriichi_doman.so',library)
        check='import sys; sys.path.insert(0,sys.argv[1]); import libriichi_doman as m; assert callable(m.arena.probe_branches)'
        subprocess.run([sys.executable,'-c',check,str(output)],cwd=output,check=True)
        manifest.update(status='built-awaiting-probe-validation',library_sha256=sha256(library),
                        tests_sha256=sha256(output/'tests.log'),build_log_sha256=sha256(output/'build.log'))
        save(); print(json.dumps(manifest),flush=True)
    except BaseException as error:
        manifest.update(status='failed',error=f'{type(error).__name__}: {error}'); save(); raise


if __name__=='__main__': main()
