// Included inside arena::game only in the isolated decision-feedback build.
#[derive(serde::Deserialize)]
struct FeedbackSpec {
    id: String,
    focal: usize,
    kyoku: u8,
    honba: u8,
    kyotaku: u8,
    scores: [i32; 4],
    haipai: [[crate::tile::Tile; 13]; 4],
    yama: Vec<crate::tile::Tile>,
    rinshan: Vec<crate::tile::Tile>,
    dora_indicators: Vec<crate::tile::Tile>,
    ura_indicators: Vec<crate::tile::Tile>,
    discards: Vec<crate::mjai::Event>,
    expected_prefix: Vec<serde_json::Value>,
    discard: crate::mjai::Event,
    future_seed: u64,
    declare: bool,
}

fn feedback_wait_for_discard(board: &mut BoardState, mut poll: Poll) -> Result<()> {
    for _ in 0..100 {
        ensure!(matches!(poll, Poll::InGame), "prefix ended before target");
        if board.agent_context().player_states.iter().any(|s| s.last_cans().can_discard) {
            return Ok(());
        }
        // The supported prefix has no calls or riichi; all reactions pass.
        poll = board.poll(Default::default())?;
    }
    anyhow::bail!("prefix did not reach a discard decision")
}

#[pyo3::pyfunction]
pub fn probe_branches(
    engine: pyo3::PyObject,
    cases: Vec<String>,
    py: pyo3::Python<'_>,
) -> Result<Vec<String>> {
    py.allow_threads(move || {
        use crate::agent::new_py_agent;
        use crate::mjai::Event;
        use sha3::{Digest, Sha3_256};
        ensure!(!cases.is_empty() && cases.len() <= 128);
        let ids: Vec<u8> = (0..cases.len()).flat_map(|_| 0..4).collect();
        let mut agents = [new_py_agent(engine, &ids)?];
        ensure!(agents[0].oracle_obs_version().is_none(), "oracle prohibited");
        let mut games = Vec::new();
        let mut metadata = Vec::new();
        for (index, encoded) in cases.iter().enumerate() {
            let spec: FeedbackSpec = serde_json::from_str(encoded)?;
            ensure!(spec.focal < 4 && spec.kyoku < 4);
            let mut board = Board {
                doman_length: 4,
                kyoku: spec.kyoku,
                honba: spec.honba,
                kyotaku: spec.kyotaku,
                scores: spec.scores,
                haipai: spec.haipai,
                yama: spec.yama,
                rinshan: spec.rinshan,
                dora_indicators: spec.dora_indicators,
                ura_indicators: spec.ura_indicators,
            }.into_state();
            let poll = board.poll(Default::default())?;
            feedback_wait_for_discard(&mut board, poll)?;
            for discard in &spec.discards {
                let actor = match discard {
                    Event::Dahai { actor, .. } => *actor as usize,
                    _ => anyhow::bail!("only discard prefixes supported"),
                };
                board.agent_context().player_states[actor].validate_reaction(discard)?;
                let mut reactions: [EventExt; 4] = Default::default();
                reactions[actor] = EventExt::no_meta(discard.clone());
                let poll = board.poll(reactions)?;
                feedback_wait_for_discard(&mut board, poll)?;
            }
            let ctx = board.agent_context();
            let root = &ctx.player_states[spec.focal];
            ensure!(root.last_cans().can_discard && root.last_cans().can_riichi);
            let public: Vec<serde_json::Value> = ctx.log.iter().map(|ev| {
                let mut value = serde_json::to_value(&ev.event).unwrap();
                match ev.event {
                    Event::StartKyoku { .. } => {
                        for p in 0..4 {
                            if p != spec.focal {
                                value["tehais"][p] = serde_json::json!(vec!["?"; 13]);
                            }
                        }
                    }
                    Event::Tsumo { actor, .. } if actor as usize != spec.focal => {
                        value["pai"] = serde_json::json!("?");
                    }
                    _ => (),
                }
                value
            }).collect();
            ensure!(public == spec.expected_prefix, "visible prefix changed");
            let (obs, mask) = root.encode_obs(4, false);
            let bytes = serde_json::to_vec(&(obs.into_raw_vec_and_offset().0, mask.to_vec()))?;
            let observation_hash = format!("{:x}", Sha3_256::digest(bytes));
            root.validate_reaction(&spec.discard)?;
            if spec.declare {
                let event = Event::Reach { actor: spec.focal as u8 };
                root.validate_reaction(&event)?;
                let mut reactions: [EventExt; 4] = Default::default();
                reactions[spec.focal] = EventExt::no_meta(event);
                ensure!(matches!(board.poll(reactions)?, Poll::InGame));
                board.agent_context().player_states[spec.focal].validate_reaction(&spec.discard)?;
            }
            let indexes = array::from_fn(|p| Index { agent_idx: 0, player_id_idx: index*4+p });
            for idx in &indexes { agents[0].start_game(idx.player_id_idx)?; }
            let mut last_reactions: [EventExt; 4] = Default::default();
            last_reactions[spec.focal] = EventExt::no_meta(spec.discard);
            games.push((index, Game {
                length: 4, doman: true, seed: (spec.future_seed, 739), indexes,
                board, kyoku: spec.kyoku, honba: spec.honba, kyotaku: spec.kyotaku,
                scores: spec.scores, kyoku_started: true, last_reactions,
                ..Default::default()
            }));
            metadata.push((spec.id, spec.focal, spec.declare, observation_hash));
        }
        let mut outputs = vec![String::new(); games.len()];
        let mut cycles = 0;
        while !games.is_empty() {
            for (_, game) in &mut games { game.poll(&mut agents)?; }
            let mut remove = Vec::new();
            for (slot, (index, game)) in games.iter_mut().enumerate() {
                if let Some(result) = game.commit(&mut agents)? {
                    let (id, focal, declare, observation_hash) = &metadata[*index];
                    outputs[*index] = serde_json::to_string(&serde_json::json!({
                        "id": id, "focal": focal, "declare": declare,
                        "observation_sha3_256": observation_hash,
                        "scores": result.scores, "ranks": result.rankings().rank_by_player,
                        "log": result.dump_json_log()?,
                    }))?;
                    remove.push(slot);
                }
            }
            for slot in remove.into_iter().rev() { games.swap_remove(slot); }
            cycles += 1;
            ensure!(cycles < 20000, "probe continuation bound exhausted");
        }
        Ok(outputs)
    })
}
