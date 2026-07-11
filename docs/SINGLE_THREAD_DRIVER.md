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
- [ ] **P1.5** *(inserted)* Build `SingleThreadDispatcher` (FIFO SynchronizationContext);
      reroute the 3 `Task.Run` escapes onto the engine thread's own queue. Gate: P1.5
      double-run bit-identical (race gone). This pulls the old P4 "delete `Task.Run`"
      forward because it is load-bearing for determinism, not cleanup.
- [x] **P1.5** Eliminated all 5 `Task.Run` thread-pool escapes. The sync context is pinned in
      the RunSimulator ctor (constructed on the engine thread), so each op runs inline on the
      engine thread — suspending on the selection TCS and resuming exactly as before, but
      single-threaded. `DrainUntilComplete` replaces `Task.Wait`. Gate: **double-run
      bit-identical (race gone)**, 5×5 25/25.
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
- [x] **P4** Deleted the interim `_executorStuckLatched` latch; `WaitForActionExecutor` is a
      plain bounded pump again. Gate: 5×5 **25/25**, 5 seeds pass, double-run bit-identical.
- [ ] **P5** Full 1000-run (`tools/m1_evaluate_1000.py`). Gate: 0 timeouts / 0 protocol errors /
      0 non-terminating episodes. Only then bump the main-repo submodule pointer.

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
