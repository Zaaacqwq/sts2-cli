# Single-Thread FIFO Driver (Headless) — Design

Status: **in progress** (see phase checklist). Owner branch: `rl-v2-protocol-state-machine`.
Related: main repo `docs/RL_V2_CLI_STATE_MACHINE.md` (protocol state machine).

## Problem

The headless CLI drives the real (async, Godot-frame-oriented) game engine on the
**CLI/stdin thread** by faking a frame loop with `InlineSynchronizationContext` +
scattered manual `Pump()` / `WaitForActionExecutor()`.

`InlineSynchronizationContext.Post` runs continuations **inline, immediately**
(`d(state)`). That inline execution is what both:

- avoids deadlock on the ~25 `xxx().GetAwaiter().GetResult()` blocking calls, **and**
- causes **re-entrant / out-of-order** continuation execution.

When a nested card selection (Necrobinder *Snap*, Regent *Begone*) resolves its inner
selection *before* the outer effect finishes committing, the engine's in-flight action
counter is decremented against the wrong frame and `ActionExecutor.IsRunning` latches
`true` for the rest of the episode. The game state stays valid, but combat can no longer
progress → the episode either times out (pre-fix) or spins to the step cap (post-latch).

Reproduces deterministically on: `Necrobinder-32`, `Regent-29`, `Regent-74`
(random A0 agent, seeds `m1-a0-<char>-<i>`).

## Goal

Replace the "scattered manual pump + inline execution" model with a **single dedicated
engine thread running a strict-FIFO dispatcher**, and detect "action complete" by
**quiescence** rather than the (corruptible) `IsRunning` flag. This structurally removes
the entire class of re-entrancy / ordering bugs.

## Architecture

```
┌─────────────┐   post(command)    ┌──────────────────────────────┐
│  CLI thread  │ ─────────────────► │        Engine thread (one)    │
│  (read stdin)│                    │  · owns ALL game state        │
│  block on rc │ ◄───────────────── │  · SingleThreadDispatcher     │
└─────────────┘   SetResult(json)   │  · strict FIFO continuations  │
                                    └──────────────────────────────┘
```

- **CLI thread** (existing `Main` loop): read line → deserialize → post a `Command` to the
  engine inbox → block on its completion → write JSON to stdout. Touches no game state.
- **Engine thread** (new, single, dedicated): the *only* thread that touches `RunManager`
  and the dispatcher. Loop:

  ```csharp
  while (running) {
      var cmd = _inbox.Take();
      try   { await cmd.ExecuteAsync(); cmd.SetResult(DetectDecisionPoint()); }
      catch (Exception e) { cmd.SetException(e); }
  }
  ```

### SingleThreadDispatcher (replaces InlineSynchronizationContext)

- A `SynchronizationContext` whose `Post` **only enqueues + signals** — never runs inline.
  This single change kills re-entrancy.
- The engine loop pulls continuations **one at a time, FIFO**.

### Quiescence (replaces IsRunning)

"An action is done" ≡ **dispatcher queue empty AND no pending card selection**. The
corruptible `ActionExecutor.IsRunning` flag is never consulted. Nested selection cannot
invert because inner continuations are only dequeued *after* outer ones, on one thread.

### Deadlock resolution (the core risk)

Under a pure FIFO dispatcher, a naive `task.GetAwaiter().GetResult()` on the engine thread
would block waiting for a continuation that can only run on the engine thread → self-deadlock.
Two mechanisms, both used:

| Site | Fix |
|---|---|
| Command main flow | Convert to real `async`/`await` so continuations return to the loop. |
| Deep sync call sites | `RunInline(Task)` helper: drain the FIFO queue *while* waiting. **Never** call it from within a continuation — the single top-level drain point guarantees zero re-entrancy. |

Discipline: an entire command executes as one async flow with **exactly one drain point**
(the engine loop). No handler blocks mid-flight.

### Card-selection round-trip

A selection spans two CLI commands. `play_card`'s async flow runs until the engine awaits
the selection TCS; that is a natural quiescent-with-input-needed point → return `card_select`.
`select_cards` sets the TCS; the loop resumes the continuation FIFO. Nested selection = two
suspend/resume round-trips, each resumed in order.

## What gets deleted (the #4 dividend)

- `InlineSynchronizationContext` inline execution.
- ~30 scattered `_syncCtx.Pump()` calls.
- `WaitForActionExecutor`'s `IsRunning` spin (and the interim `_executorStuckLatched`).
- 3 `Task.Run(...)` "nuclear" hacks (end-turn / shop) — they existed to escape the broken
  pump; cross-thread state access is unsafe here and unnecessary once the loop is correct.

## New files

- `EngineThread.cs` — dedicated thread, inbox, message loop.
- `SingleThreadDispatcher.cs` — FIFO synchronization context.
- `Quiescence.cs` — quiescence detection + `RunInline` helper.

## Phases (each validated with the 3 reproducing seeds)

- [ ] **P0** Freeze baseline: 5×5 regression, deterministic state hash, 3-seed status.
- [ ] **P1** Scaffold engine thread + FIFO dispatcher; commands serialized through it but
      internals still use the old sync logic. Gate: 5×5 regression unchanged.
- [ ] **P2** Migrate read paths (`start_run`/`map`/pure combat) to async; drop their `Pump`.
      Gate: same-seed state hash bit-identical to P0 (determinism preserved).
- [ ] **P3** Migrate selection round-trip to the quiescence model; drop `IsRunning` reliance.
      Gate: `Necrobinder-32`, `Regent-29`, `Regent-74` reach `game_over`, per-step < 50ms.
- [ ] **P4** Delete `Task.Run` hacks, `WaitForActionExecutor`, latch. Gate: 5×5 + 3 seeds pass.
- [ ] **P5** Full 1000-run (`tools/m1_evaluate_1000.py`). Gate: 0 timeouts / 0 protocol errors /
      0 non-terminating episodes. Only then bump the main-repo submodule pointer.

## Test matrix (run every phase)

- Correctness: 5 characters × 5 games all `Completed` (`CLAUDE.md` gate).
- Determinism: same-seed double reset → bit-identical state hash (`rl/tests/test_seeds.py`).
- Root cause: 3 reproducing seeds reach `game_over`, single step < 50ms.
- Protocol: `rl/tests/test_protocol.py`, `test_engine_trace.py`.
- Throughput: 1/4/8/16-worker benchmark ≥ current (8w ≥ 100 decision steps/s).

## Rollback

- All changes on fork branch `rl-v2-protocol-state-machine`; main-repo submodule pointer
  bumps **only after P5**.
- P1–P4 are independent commits; revert any phase individually.
- Safety net: keep the stuck-detection "abandon episode" change (option ⑥) as a separate
  commit so training never wedges even if #4 misses a corner.
