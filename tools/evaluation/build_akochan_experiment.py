"""Isolated Akochan and Doman adapter build; no live files are changed."""
import hashlib, io, json, os, pathlib, shutil, subprocess, sys, tarfile, time
R=pathlib.Path('/mnt/e/SapphireServer/FFXIV-AutoMahjongSolver')
sys.path.insert(0,str(R/'tools/evaluation'))
from doman_engine import BASE_COMMIT, PATCH, DATASET_PATCH, sha256
O=R/'artifacts/akochan-20261006'; A=O/'upstream'; B=O/'native'; S=B/'source'
def run(cmd,cwd,log,env=None):
    print('Running '+log,flush=True)
    with (O/log).open('w') as f: subprocess.run(cmd,cwd=cwd,env=env,stdout=f,stderr=subprocess.STDOUT,check=True,timeout=1200)
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=A,text=True).strip()=='53188a0b926fbab38177f88c3cd87d554cf412af'
if not (O/'deps/root/usr/include/boost/asio.hpp').exists():
    for p in sorted((O/'deps').glob('*.deb')): subprocess.run(['dpkg-deb','-x',str(p),str(O/'deps/root')],check=True)
inc=O/'deps/root/usr/include'; lib=O/'deps/root/usr/lib/x86_64-linux-gnu'
env={**os.environ,'CPLUS_INCLUDE_PATH':str(inc),'LIBRARY_PATH':str(lib)}
run(['make','-f','Makefile_Linux','-j2','NPROCS=2','COMPILER=g++ -include cstdint'],A/'ai_src','ako-ai-build.log',env)
# Compile the verbatim official pipe branch alone. The unused TCP client
# depends on removed Boost APIs; neither it nor the upstream simulator is needed.
main=(A/'main.cpp').read_text(); begin='    } else if (argc == 4 && strcmp(argv[1], "pipe") == 0) {'
end='    } else if (argc == 4 && strcmp(argv[1], "pipe_detailed") == 0) {'
body=main.split(begin)[1].split(end)[0]
pipe=O/'pipe_main.cpp'
pipe.write_text('#include "import.hpp"\nint main(int argc,char* argv[]) {\nif(argc!=4) return 2;\n'+body+'\nreturn 0;\n}\n')
run(['g++','-include','cstdint','-std=c++11','-O3','-fopenmp','-pthread','-I'+str(A),str(pipe),'-L'+str(A),'-lai','-lboost_system','-o',str(A/'system.exe')],A,'ako-pipe-build.log',env)
B.mkdir(); S.mkdir()
archive=subprocess.check_output(['git','-C',str(R/'.loacl/Mortal'),'archive',BASE_COMMIT])
with tarfile.open(fileobj=io.BytesIO(archive)) as t: t.extractall(S,filter='data')
subprocess.run(['git','init','--quiet'],cwd=S,check=True)
for p in (PATCH,DATASET_PATCH): subprocess.run(['git','apply',str(p)],cwd=S,check=True)
p=S/'libriichi/src/agent/akochan.rs'; code=p.read_text()
code=code.replace('event: events[i].event.clone(),','event: visible_event(&events[i].event, self.player_id),')
code=code.replace('"kyoku_first": 0,','"kyoku_first": 4,')
code += '''

// Offline Doman comparison: supply only this seat's visible information.
fn visible_event(source: &Event, player_id: u8) -> Event {
    let mut event = source.clone();
    let unknown = crate::tile::Tile::try_from(37usize).unwrap();
    match &mut event {
        Event::StartKyoku { tehais, .. } => {
            for (id, hand) in tehais.iter_mut().enumerate() {
                if id != player_id as usize { *hand = [unknown; 13]; }
            }
        },
        Event::Tsumo { actor, pai } if *actor != player_id => *pai = unknown,
        Event::StartGame { seed, .. } => *seed = None,
        _ => (),
    }
    event
}

#[cfg(test)]
mod visibility_tests {
    use super::*;
    #[test]
    fn hidden_tiles_do_not_change_input() {
        let a: Event = json::from_value(json::json!({"type":"start_kyoku", "bakaze":"E", "dora_marker":"1m", "kyoku":1,"honba":0,"kyotaku":0,"oya":0,"scores":[25000;4],"tehais":[["1m";13],["2m";13],["3m";13],["4m";13]]})).unwrap();
        for pid in 0..4u8 {
            let mut b=a.clone();
            if let Event::StartKyoku { tehais, .. } = &mut b {
                for (id, hand) in tehais.iter_mut().enumerate() {
                    if id != pid as usize { *hand=[crate::tile::Tile::try_from(8usize).unwrap();13]; }
                }
            }
            assert_eq!(visible_event(&a,pid),visible_event(&b,pid));
            if let Event::StartKyoku { tehais, .. }=visible_event(&a,pid) {
                for id in 0..4 { assert_eq!(tehais[id][0].is_unknown(), id != pid as usize); }
            }
            for actor in 0..4u8 {
                let e=Event::Tsumo{actor,pai:crate::tile::Tile::try_from(0usize).unwrap()};
                if let Event::Tsumo{pai,..}=visible_event(&e,pid) { assert_eq!(pai.is_unknown(),actor!=pid); }
            }
        }
    }
}
'''
# serde_json does not support repeat-array literal inside json!.
code=code.replace('[25000;4]','vec![25000;4]').replace('[["1m";13],["2m";13],["3m";13],["4m";13]]','[vec!["1m";13],vec!["2m";13],vec!["3m";13],vec!["4m";13]]')
p.write_text(code)
diff=subprocess.run(['diff','-u',str(R/'artifacts/doman-engine-v2-dataset/source/libriichi/src/agent/akochan.rs'),str(p)],capture_output=True)
(O/'native-adapter.patch').write_bytes(diff.stdout)
target=R/'artifacts/doman-engine-v2-dataset/source/target'
env={**os.environ,'PYO3_PYTHON':sys.executable,'CARGO_TARGET_DIR':str(target)}
run(['cargo','test','--locked','--offline','-p','libriichi','--lib','--no-default-features','visibility_tests'],S,'visibility-tests.log',env)
run(['cargo','test','--locked','--offline','-p','libriichi','--lib','--no-default-features','arena::'],S,'arena-tests.log',env)
run(['cargo','build','--release','--locked','--offline','-p','libriichi','--lib'],S,'native-build.log',env)
shutil.copy2(target/'release/libriichi_doman.so',B/'libriichi_doman.so')
manifest=dict(status='built-awaiting-pilot',akochan_commit='53188a0b926fbab38177f88c3cd87d554cf412af',base_commit=BASE_COMMIT,
    library_sha256=sha256(B/'libriichi_doman.so'),ako_binary_sha256=sha256(A/'system.exe'),ako_library_sha256=sha256(A/'libai.so'),
    tactics_sha256=sha256(A/'setup_mjai.json'),adapter_sha256=sha256(p),rules_patch_sha256=sha256(PATCH),dataset_patch_sha256=sha256(DATASET_PATCH),
    nprocs=2,omp_threads=2,live_modified=False,kyoku_first=4,pipe_source_sha256=sha256(pipe),compiler_extra='-include cstdint',boost='1.88.0-1.4ubuntu1',
    parameters={str(p.relative_to(A)):sha256(p) for p in sorted((A/'params').rglob('*')) if p.is_file()})
(O/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps({k:v for k,v in manifest.items() if k!='parameters'}),flush=True)
