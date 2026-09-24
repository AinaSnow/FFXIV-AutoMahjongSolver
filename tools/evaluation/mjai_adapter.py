"""libriichi mjai-log adapter. Censors hidden tiles before crossing the policy process boundary."""
import copy
import json
import subprocess
from rule_profile import rule_profile

HONORS = ("E", "S", "W", "N", "P", "F", "C")


def tile_name(index):
    if index >= 34:
        return "5" + "mps"[index - 34] + "r"
    return HONORS[index - 27] if index >= 27 else str(index % 9 + 1) + "mps"[index // 9]


def public_events(events, player):
    result = []
    for source in events:
        e = copy.deepcopy(source)
        e.pop("meta", None)
        kind = e["type"]
        if kind == "start_kyoku":
            e["tehais"] = [e["tehais"][player]] + [["?"] * 13 for _ in range(3)]
            e["scores"] = [e["scores"][(player + i) % 4] for i in range(4)]
        if kind == "tsumo" and e["actor"] != player:
            e["pai"] = "?"
        for field in ("actor", "target", "oya"):
            if field in e:
                e[field] = (e[field] - player) % 4
        result.append(e)
    return result


def available_hand(state):
    tiles = []
    for i, count in enumerate(state.tehai):
        tiles.extend([tile_name(i)] * count)
    for suit, red in enumerate(state.akas_in_hand):
        if red:
            tiles.remove("5" + "mps"[suit])
            tiles.append("5" + "mps"[suit] + "r")
    return tiles


def consume(hand, kinds):
    remaining = hand[:]
    result = []
    for tile in kinds:
        match = next((x for x in remaining if x == tile), None)
        if match is None:
            match = next(x for x in remaining if x.rstrip("r") == tile.rstrip("r"))
        remaining.remove(match)
        result.append(match)
    return result


def legal_actions(state, player):
    cans = state.last_cans
    hand = available_hand(state)
    flags = 0
    for attr, value in (("can_discard",1),("can_riichi",2),("can_tsumo_agari",4),
                        ("can_ron_agari",8),("can_pon",16),("can_chi",32),
                        ("can_ankan",64),("can_daiminkan",128),("can_kakan",256),("can_pass",512)):
        if getattr(cans, attr):
            flags |= value
    mask = state.encode_obs(4, False)[1][:37] if cans.can_discard else [False] * 37
    discards = [tile_name(i) for i, enabled in enumerate(mask) if enabled]
    claimed = state.last_kawa_tile()
    target = (cans.target_actor - player) % 4
    calls = []
    def add(kind, tile, consumed, from_seat=target):
        calls.append(dict(kind=kind, pai=tile, consumed=consume(hand, consumed), target=from_seat))
    if cans.can_pon:
        add("Pon", claimed, [claimed.rstrip("r")] * 2)
    if cans.can_daiminkan:
        add("MinKan", claimed, [claimed.rstrip("r")] * 3)
    if cans.can_chi:
        n, suit = int(claimed[0]), claimed[1]
        for attr, offsets in (("can_chi_low",(1,2)),("can_chi_mid",(-1,1)),("can_chi_high",(-2,-1))):
            if getattr(cans,attr):
                add("Chi",claimed,[str(n+d)+suit for d in offsets])
    if cans.can_ankan:
        for tile in state.ankan_candidates():
            add("AnKan",tile,[tile.rstrip("r")] * 4,-1)
    if cans.can_kakan:
        for tile in state.kakan_candidates():
            add("ShouMinKan",tile,[tile.rstrip("r")],-1)
    return dict(flags=flags,discards=discards,calls=calls)


class PolicyEngine:
    engine_type = "mjai-log"
    def __init__(self, command, name="stable", enhanced=False, calibration_path=None, match_mode="libriichi-hanchan"):
        self.rules = rule_profile(match_mode)
        self.name, self.enhanced = name, enhanced
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
        self.results = []
        self.samples = []
        self.calibration_path = calibration_path
    def set_player_ids(self, ids):
        self.player_ids = ids
    def start_game(self, index):
        pass
    def end_kyoku(self, index):
        pass
    def end_game(self, index, scores):
        player = self.player_ids[index]
        # libriichi uses starting seat order to break tied scores.
        rank = sorted(range(4), key=lambda i:(-scores[i],i)).index(player) + 1
        self.results.append(dict(index=index,seat=player,scores=scores,rank=rank,score=scores[player]))
    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
    def react_batch(self, game_states):
        results = []
        for game in game_states:
            player = self.player_ids[game.game_index]
            events = public_events(json.loads(game.events_json),player)
            legal = legal_actions(game.state,player)
            request = dict(events=events,legal=legal,scheduled_rounds=self.rules["scheduled_rounds"],match_mode=self.rules["match_mode"],enhanced=self.enhanced,calibration_path=self.calibration_path)
            self.process.stdin.write(json.dumps(request)+"\n")
            self.process.stdin.flush()
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("policy process exited")
            decision = json.loads(line)
            if "error" in decision:
                raise RuntimeError(decision["error"])
            kind = decision["kind"]
            if decision.get("calibration_key"):
                start=events[0]
                self.samples.append(dict(game_index=game.game_index,key=decision["calibration_key"],
                    hand_key=f"{start['bakaze']}:{start['kyoku']}:{start['honba']}"))
            if kind in ("Tsumo","Ron"):
                result = dict(type="hora",actor=player,target=player if kind=="Tsumo" else game.state.last_cans.target_actor)
            elif kind == "Riichi":
                result = dict(type="reach",actor=player)
            elif kind == "Discard":
                tile = decision["tile"]
                if tile not in legal["discards"]:
                    raise RuntimeError(f"policy selected illegal discard or wrong red identity: {tile}")
                result = dict(type="dahai",actor=player,pai=tile,tsumogiri=events[-1]["type"]=="tsumo" and events[-1].get("actor")==0 and tile==game.state.last_self_tsumo())
            elif kind == "Pass":
                result = dict(type="none")
            else:
                chosen = decision["call"]
                call = next(c for c in legal["calls"] if c["kind"]==chosen["kind"] and c["pai"].rstrip("r")==chosen["pai"] and sorted(x.rstrip("r") for x in c["consumed"])==sorted(chosen["consumed"]))
                type_name = dict(Pon="pon",Chi="chi",MinKan="daiminkan",AnKan="ankan",ShouMinKan="kakan")[kind]
                result = dict(type=type_name,actor=player,consumed=call["consumed"])
                if kind != "AnKan":
                    result.update(pai=call["pai"],target=(call["target"]+player)%4)
                if kind == "ShouMinKan":
                    # Original pon consists entirely of publicly exposed tiles.
                    pon = next(e for e in reversed(events) if e["type"]=="pon" and e["actor"]==0 and e["pai"].rstrip("r")==call["pai"].rstrip("r"))
                    result.update(pai=call["consumed"][0],consumed=pon["consumed"]+[pon["pai"]])
                    result.pop("target",None)
            results.append(json.dumps(result))
        return results
