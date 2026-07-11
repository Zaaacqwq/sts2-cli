# Single-Thread FIFO Driver (Headless) — Design

Status: **P5 complete (2026-07-11)**. Owner branch: `rl-v2-protocol-state-machine`.
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
- **Engine thread** (new, single, dedicated): owns `RunManager` and the dispatcher.
  Commands are serialized through its inbox.

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

The game also exposes a synchronous `ICardSelector.GetSelectedCardReward` contract. An
event/rest/shop operation can enter that method before returning a `Task`, so running it
directly on the engine thread self-deadlocks before the CLI can surface the selection.
Those four call sites use one explicitly tracked blocking bridge task. Only one bridge may
exist; after the CLI resolves its external selection, the engine thread joins it before
accepting another state-mutating command. All captured engine continuations still use the
FIFO dispatcher. Removing this final bridge requires a protocol-level suspended-work-item
broker, not merely replacing `.GetResult()` with `await`.

### Card-selection round-trip

A selection spans two CLI commands. `play_card`'s async flow runs until the engine awaits
the selection TCS; that is a natural quiescent-with-input-needed point → return `card_select`.
`select_cards` sets the TCS; the loop resumes the continuation FIFO. Nested selection = two
suspend/resume round-trips, each resumed in order.

## What was deleted/replaced

- `InlineSynchronizationContext` inline execution was replaced by
  `SingleThreadDispatcher`; `Post` now only enqueues.
- `WaitForActionExecutor` no longer reads or spins on `ActionExecutor.IsRunning`; it drains
  FIFO work to quiescence and joins a resolved blocking bridge.
- The end-turn thread-pool fallback and stuck-executor latch were removed.
- Four selector-capable event/rest/shop bridges remain for the synchronous selector API,
  with explicit single-flight tracking and joining.

## New files

- `EngineThread.cs` — dedicated thread, inbox, message loop.
- `SingleThreadDispatcher.cs` — FIFO synchronization context.
- `Quiescence.cs` — quiescence detection + `RunInline` helper.

## Phases (each validated with the 3 reproducing seeds)

- [x] **P0** Freeze baseline: 5×5 regression (25/25), deterministic argmin state-hash
      anchor (`rl/schema/p0_baseline_hash.json`, double-run bit-identical), 3-seed status.
      Finding: the engine is deterministic under a fixed action sequence; *stochastic*
      rollouts are **not** process-stable because the engine emits candidate lists whose
      order/length vary per process (.NET string-hash randomization) and `random.choice`
      draws a length-dependent number of RNG bits. Anchor therefore uses a zero-RNG argmin.
- [x] **P1** Scaffold engine thread (`EngineThread.cs`); every command marshalled onto one
      dedicated thread. Gate: 5×5 regression **25/25 pass**.
      **Finding (drives P1.5):** moving engine logic off the main thread exposed a *latent
      race*. P0 double-run was bit-identical; P1 double-run diverges. Cause: the old
      internals still use 3 `Task.Run(...)` escapes to the thread pool. On the main thread
      the pump loop serialized them; on a dedicated engine thread the engine/pool
      scheduling is now nondeterministic. **Half-measures don't work — thread-pool escapes
      must be removed to get true single-thread determinism.** Games still complete (race
      changes outcomes, not liveness), consistent with determinism being a P2+ gate.
- [x] **P1.5** Added `SingleThreadDispatcher`; `Post` is non-inline and FIFO. Removed the
      end-turn escape. Testing exposed that four event/rest/shop APIs can synchronously enter
      `GetSelectedCardReward`; these use the tracked single-flight bridge described above.
- [x] **P3 — ROOT CAUSE FOUND AND CURED.** Not a timing/quiescence problem at all:
      `HeadlessCardSelector.ResolvePending` completed the selection TCS and *then* nulled
      `PendingOptions`/`_pendingTcs`. Completing the TCS runs the card's continuation
      **inline** (the sync context executes posted continuations immediately), and for a
      nested-select card (Necrobinder **Snap**, Regent **Begone**) that continuation re-enters
      `GetSelectedCards` and registers a **second** selection — which the trailing nulls then
      **wiped**. `HasPending` went false, so the CLI never surfaced the `card_select`, while the
      engine awaited the orphaned TCS forever: `ActionExecutor.IsRunning` latched true and every
      later combat action became a silent no-op — the frozen combat behind the "stuck seeds".
      **Fix: clear the fields *before* completing the TCS** (+ surface a nested selection from
      `DoSelectCards`). Gate: Necrobinder-32 / Regent-29 / Regent-74 went from a 2000-step
      frozen spin (~203s) to `game_over` in 60–65 steps (~1s). All 5 formerly-failing seeds
      pass with `error: null`.
- [x] **P4** Deleted the interim `_executorStuckLatched` latch and removed
      `ActionExecutor.IsRunning` from command-completion detection. Gate: five reproducing
      seeds and the 200-episode preflight pass.
- [x] **P5** Full persistent-worker run (`tools/m1_evaluate_1000.py`): **1000/1000** reached
      `game_over`, 0 timeouts, 0 protocol errors, 0 non-terminating episodes, 280.0 seconds
      with 6 workers. Reset determinism matched across repeated, interleaved-character, and
      separate-worker runs; no orphan engine processes remained.

## Note on the original diagnosis

The upstream notes attributed the wedge to a `Thread.Sleep(1)` timer-resolution issue making
`WaitForActionExecutor` spin ~31s, and treated the stuck `ActionExecutor.IsRunning` as an
un-root-caused engine bug ("止损 not 根治"). That was a symptom. The executor was stuck because
it was legitimately awaiting a card selection that the selector had destroyed. Fixing the
clear/complete ordering removes the wedge entirely — no latch, no cancel, no timeout tricks.

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
