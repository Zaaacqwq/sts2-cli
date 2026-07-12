"""Tests for the RL curriculum protocol: start_combat and list_models."""

COMBAT = {
    "cmd": "start_combat", "character": "Ironclad", "seed": "curr-1",
    "ascension": 0, "encounter": "SLIMES_WEAK",
    "player": {
        "hp": 50, "max_hp": 70, "gold": 250,
        "deck": ["STRIKE_IRONCLAD"] * 4 + ["DEFEND_IRONCLAD"] * 4,
        "relics": ["BURNING_BLOOD"], "potions": ["BLOCK_POTION"],
    },
}


class TestStartCombat:
    def test_atomic_reset_applies_player_overrides(self, game):
        state = game.send(COMBAT)
        assert state["decision"] == "combat_play"
        player = state["player"]
        assert player["hp"] == 50
        assert player["max_hp"] == 70
        assert player["gold"] == 250
        assert player["deck_size"] == 8
        assert [r["name"] for r in player["relics"]] == ["Burning Blood"]
        assert len(player["potions"]) == 1
        assert state["enemies"]

    def test_same_seed_same_process_is_deterministic(self, game):
        first = game.send(COMBAT)
        second = game.send(COMBAT)
        for key in ("hand", "enemies", "energy", "round"):
            assert first[key] == second[key]

    def test_unknown_encounter_fails_closed(self, game):
        state = game.send({**COMBAT, "encounter": "NOT_A_REAL_ENCOUNTER"})
        assert state["type"] == "error"

    def test_unknown_relic_fails_closed(self, game):
        state = game.send({**COMBAT, "player": {"relics": ["NOT_A_RELIC"]}})
        assert state["type"] == "error"

    def test_missing_encounter_fails_closed(self, game):
        cmd = {k: v for k, v in COMBAT.items() if k != "encounter"}
        state = game.send(cmd)
        assert state["type"] == "error"

    def test_start_run_after_start_combat_is_clean(self, game):
        game.send(COMBAT)
        state = game.start(character="Silent", seed="after-combat")
        assert state["decision"] == "event_choice"

    def test_abandoned_reward_does_not_pollute_next_reset(self, game):
        # Win a combat, leave the card_reward unconsumed, then reset: the new
        # episode must open on its own first combat decision, not the stale
        # reward screen of the previous episode.
        state = game.send(COMBAT)
        for _ in range(120):
            if state.get("decision") != "combat_play":
                break
            state = game.auto_combat(state)
        assert state.get("decision") == "card_reward"
        fresh = game.send({**COMBAT, "seed": "curr-fresh"})
        assert fresh["decision"] == "combat_play"
        clean_run = game.start(character="Ironclad", seed="curr-run-after-reward")
        assert clean_run["decision"] == "event_choice"

    def test_combat_is_playable_to_the_end(self, game):
        state = game.send(COMBAT)
        for _ in range(60):
            if state.get("decision") != "combat_play":
                break
            state = game.auto_combat(state)
        assert state.get("decision") != "combat_play"
        assert state.get("type") != "error"


class TestListModels:
    def test_encounters_carry_act_and_category(self, game):
        result = game.send({"cmd": "list_models", "kind": "encounter"})
        assert result["type"] == "model_list"
        rows = result["models"]
        assert rows
        categories = {r["category"] for r in rows}
        assert categories == {"weak", "regular", "elite", "boss"}
        assert {r["act"] for r in rows} >= {1, 2, 3}
        act1_weak = [r["id"] for r in rows if r["act"] == 1 and r["category"] == "weak"]
        assert "SLIMES_WEAK" in act1_weak

    def test_cards_have_type_and_rarity(self, game):
        result = game.send({"cmd": "list_models", "kind": "card"})
        rows = result["models"]
        assert any(r["id"] == "STRIKE_IRONCLAD" for r in rows)
        assert all("type" in r and "rarity" in r for r in rows)

    def test_unknown_kind_fails_closed(self, game):
        result = game.send({"cmd": "list_models", "kind": "nonsense"})
        assert result["type"] == "error"
