// Included only in the isolated natural endgame experiment library.
use pyo3::prelude::*;
use crate::mjai::Event;

fn natural_agents(engine: &PyObject) -> Result<[Box<dyn BatchAgent>; 1]> {
    let obj = Python::with_gil(|py| engine.clone_ref(py));
    let mut agents = [crate::agent::new_py_agent(obj, &[0,1,2,3])?];
    ensure!(agents[0].oracle_obs_version().is_none(), "oracle prohibited");
    for i in 0..4 { agents[0].start_game(i)?; }
    Ok(agents)
}

fn natural_visible(game: &Game, focal: usize) -> Result<serde_json::Value> {
    use sha3::{Digest, Sha3_256};
    let ctx = game.board.agent_context();
    let prefix: Vec<_> = ctx.log.iter().map(|e| {
        let mut v = serde_json::to_value(&e.event).unwrap();
        match e.event {
            Event::StartKyoku {..} => for p in 0..4 { if p != focal { v["tehais"][p] = serde_json::json!(vec!["?";13]); } },
            Event::Tsumo {actor,..} if actor as usize != focal => v["pai"] = serde_json::json!("?"),
            _ => (),
        }
        v
    }).collect();
    let mut scores = game.scores;
    for e in ctx.log { if let Event::ReachAccepted {actor} = e.event { scores[actor as usize] -= 1000; } }
    let s = &ctx.player_states[focal];
    let (obs,mask)=s.encode_obs(4,false);
    let bytes=serde_json::to_vec(&(obs.into_raw_vec_and_offset().0,mask.to_vec()))?;
    Ok(serde_json::json!({"focal":focal,"prefix":prefix,"scores":scores,"turn":s.at_turn(),
        "shanten":s.shanten(),"can_riichi":s.last_cans().can_riichi,
        "observation_sha3_256":format!("{:x}",Sha3_256::digest(bytes))}))
}

fn natural_eligible(game: &Game) -> Option<usize> {
    if game.ended || !game.kyoku_started || game.kyoku != 3 { return None; }
    let ctx=game.board.agent_context();
    let focal=match ctx.log.last()?.event { Event::Tsumo {actor,..}=>actor as usize,_=>return None };
    let mut scores=game.scores;
    let mut kans=0;
    for e in ctx.log { match e.event {
        Event::ReachAccepted {actor}=>scores[actor as usize]-=1000,
        Event::Ankan {..}|Event::Kakan {..}|Event::Daiminkan {..}=>kans+=1,
        _=>(),
    } }
    let s=&ctx.player_states[focal]; let c=s.last_cans();
    (scores.iter().all(|&v|v<30000) && s.shanten()<=1 && !s.self_riichi_declared()
        && !c.can_tsumo_agari && c.can_discard && kans<4).then_some(focal)
}

fn natural_finish(game: &mut Game, agents: &mut [Box<dyn BatchAgent>]) -> Result<serde_json::Value> {
    for _ in 0..20000 {
        game.poll(agents)?;
        if let Some(result)=game.commit(agents)? {
            return Ok(serde_json::json!({"scores":result.scores,"ranks":result.rankings().rank_by_player,"log":result.dump_json_log()?}));
        }
    }
    anyhow::bail!("continuation bound exhausted")
}

#[pyfunction]
pub fn natural_endgame(engine: PyObject, callback: PyObject, seed: u64, key: u64, select_root: bool, control: bool, py: Python<'_>) -> Result<String> {
    py.allow_threads(move || {
        let mut agents=natural_agents(&engine)?;
        let mut game=Game {length:4,doman:true,seed:(seed,key),scores:[25000;4],
            indexes:array::from_fn(|p|Index{agent_idx:0,player_id_idx:p}),..Default::default()};
        let mut root_game: Option<Game>=None;
        let mut root=serde_json::Value::Null;
        let mut choices:Vec<Vec<Event>>=Vec::new();
        let mut mortal_moves:Vec<Event>=Vec::new();
        let mut focal=0;
        let mut need_mortal_discard=false;
        let mut baseline=None;
        for _ in 0..20000 {
            game.poll(&mut agents)?;
            let picking=if select_root && root_game.is_none() { natural_eligible(&game) } else { None };
            if let Some(p)=picking {
                focal=p; root=natural_visible(&game,p)?;
                let request=serde_json::to_string(&root)?;
                let response: String=Python::with_gil(|py| callback.call1(py,(request,))?.extract(py))?;
                choices=serde_json::from_str(&response)?;
                ensure!(choices.len()==2);
            }
            if let Some(result)=game.commit(&mut agents)? {
                baseline=Some(serde_json::json!({"scores":result.scores,"ranks":result.rankings().rank_by_player,"log":result.dump_json_log()?}));
                break;
            }
            if picking.is_some() {
                mortal_moves.push(game.last_reactions[focal].event.clone());
                need_mortal_discard=matches!(mortal_moves[0],Event::Reach{..});
                root_game=Some(game.clone());
            } else if need_mortal_discard {
                let event=game.last_reactions[focal].event.clone();
                ensure!(matches!(event,Event::Dahai{..}),"expected riichi discard");
                mortal_moves.push(event); need_mortal_discard=false;
            }
        }
        let baseline=baseline.ok_or_else(||anyhow::anyhow!("natural game bound exhausted"))?;
        if root_game.is_none() { return Ok(serde_json::to_string(&serde_json::json!({"seed":seed,"root":null,"baseline":baseline}))?); }
        let saved=root_game.unwrap();
        root["mortal_moves"]=serde_json::to_value(&mortal_moves)?;
        let mut results=vec![baseline.clone()];
        let mut actions=vec![mortal_moves.clone()];
        if control { choices=vec![mortal_moves.clone(),mortal_moves.clone()]; }
        for moves in choices {
            ensure!(!moves.is_empty() && moves.len()<=2);
            if !control {
                if let Some(i)=actions.iter().position(|m|serde_json::to_value(m).unwrap()==serde_json::to_value(&moves).unwrap()) {
                    let mut reused=results[i].clone(); reused["reused"]=serde_json::json!(i);
                    results.push(reused); actions.push(moves); continue;
                }
            }
            let mut branch=saved.clone();
            let mut branch_agents=natural_agents(&engine)?;
            ensure!(natural_visible(&branch,focal)?=={let mut r=root.clone();r.as_object_mut().unwrap().remove("mortal_moves");r});
            for (i,event) in moves.iter().enumerate() {
                branch.board.agent_context().player_states[focal].validate_reaction(event)?;
                let mut reactions:[EventExt;4]=Default::default();
                reactions[focal]=EventExt::no_meta(event.clone());
                if i+1<moves.len() {
                    ensure!(matches!(event,Event::Reach{..}),"two-stage action must be riichi");
                    ensure!(matches!(branch.board.poll(reactions)?,Poll::InGame));
                } else { branch.last_reactions=reactions; }
            }
            results.push(natural_finish(&mut branch,&mut branch_agents)?); actions.push(moves);
        }
        Ok(serde_json::to_string(&serde_json::json!({"seed":seed,"root":root,"actions":actions,"results":results}))?)
    })
}
