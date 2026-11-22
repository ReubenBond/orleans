# Third-party notices

This file documents third-party material referenced by, or under evaluation for
adaptation into Clockwork and the licensing policy for each. Adapted material is listed below with
its source and modification summary.

If and when code or tests are adapted from any of the projects below, this file
must be updated in the same change with:

- The specific files/functions adapted and their origin (repository, path, commit).
- The original license text (or a pointer to it) reproduced per that license's
  attribution requirements.
- Any modifications made, clearly distinguished from the original.

## Projects under evaluation

### Microsoft Coyote

- **License:** MIT
- **Repository:** https://github.com/microsoft/coyote
- **Why it's relevant:** Coyote implements systematic concurrency testing
  (controlled scheduling, race exploration) for .NET, overlapping with Clockwork's
  "controlled mode" and "race exploration mode" (see
  [docs/compatibility.md](docs/compatibility.md)). Its test suite is also a useful
  reference for characterizing scheduler edge cases.
- **Adaptation policy:** MIT permits reuse with attribution and retention of the
  copyright/license notice. Any Coyote-derived source or test adapted into this
  repository must retain Coyote's copyright notice in the adapted file (or in this
  document, referencing the file) and note the adaptation here. Wholesale copying
  without attribution is not permitted; substantial adaptation should credit Coyote
  in code comments at the adaptation site as well.
- **Adapted rewrite-engine material:** The following files in
  `src/Orleans.Testing.Simulation.Instrumentation/Rewriting/` adapt portions of Coyote's Mono.Cecil
  rewriting engine and carry Coyote's copyright/license header at the top of the
  file, per the policy above:
  - `RewritePass.cs` — adapts the IL visitor traversal
    (`VisitAssembly`/`Module`/`Type`/`Method`/`Instruction`), the `Replace` helper
    that fixes up branch targets and exception-handler boundaries, and the
    `SimplifyMacros`/`OptimizeMacros` offset-fix pattern from Coyote's
    `Source/Test/Rewriting/Passes/Pass.cs` and
    `Source/Test/Rewriting/Passes/Rewriting/RewritingPass.cs`.
  - `AssemblyRewriteContext.cs` — adapts Coyote's assembly load/write, symbol
    detection, and rewrite-signature-attribute handling (from Coyote's
    `AssemblyInfo`/rewriting-engine assembly handling).

  Clockwork-specific changes are noted in each file header and are developed in
  commits separate from the mechanical adaptation.

- **Adapted controlled task/async material:** The controlled task/async machinery
  (`src/Orleans.Testing.Simulation/Runtime/Tasks/` and `.../Tasks/CompilerServices/`, and the
  member-aware substitution pass in `src/Orleans.Testing.Simulation.Instrumentation/Rewriting/`) is a
  **design-level adaptation** of Coyote's controlled-task model — its
  `Microsoft.Coyote.Runtime.CompilerServices` builder/awaiter types
  (`AsyncTaskMethodBuilder`, `TaskAwaiter`, `ConfiguredTaskAwaitable`, `YieldAwaitable`,
  the `AsyncValueTaskMethodBuilder`, `ValueTaskAwaiter`, and `ConfiguredValueTaskAwaitable`
  value-task equivalents, and their awaiters), its `Microsoft.Coyote.Runtime.CompilerServices`
  rewriting pass
  that retargets compiler-generated state machines, and the shape of its controlled
  task/awaiter tests. **No Coyote source was copied verbatim into these files:** unlike
  Coyote's from-scratch task reimplementation, Clockwork's controlled builders/awaiters
  are thin value-type wrappers that forward to the real BCL builder/awaiter and only
  redirect *where the continuation is scheduled* (to Clockwork's
  `ISimulationTaskCoordinator` rather than Coyote's `CoyoteRuntime`). The conformance
  tests are original (they compile and rewrite real state machines with Roslyn) rather
  than ports of Coyote's test source. Because the approach — not literal source — was
  adapted, these files do not carry a Coyote copyright header; this entry records the
  design lineage per the attribution policy above. Control parity is claimed **only**
  for the exact signatures enumerated in
  [docs/rule-inventory.md](docs/rule-inventory.md), not for Coyote's full surface.

- **Adapted exception-hardening material:** The exception-handler hardening pass,
  `src/Orleans.Testing.Simulation.Instrumentation/Rewriting/ExceptionHardeningRewritingPass.cs`, is a
  **source-level adaptation** of Coyote's
  `Source/Test/Rewriting/Passes/Rewriting/ExceptionFilterRewritingPass.cs` and carries a
  Coyote copyright/license attribution header at the top of the file, per the policy
  above. It reuses Coyote's algorithm for deciding which handlers to instrument (only
  broad `catch (object)`/`catch (Exception)` blocks and exception `filter`s; skipping
  finally/fault blocks, rethrow-only handlers, and compiler-generated
  async-state-machine `SetException` handlers) and Coyote's guard-injection shape
  (`dup; call guard` at the handler start with the adjacent-handler boundary fix-up).
  Clockwork-specific changes are noted in the file header: the guard is resolved through
  Clockwork's shared replacement resolver rather than Coyote's `typeof`-based import; the
  injected guard rethrows Clockwork's internal `ControlledOperationAbortSignal` rather
  than Coyote's `ExecutionCanceledException`/`ThreadInterruptedException`; the
  async-state-machine detection also recognises Clockwork's substituted controlled
  builder types; and every hardened handler is recorded as a manifest transformation.
  The parity comparison of Clockwork's thread/thread-pool/task/Parallel surface
  against Coyote is enumerated in [docs/coyote-parity.md](docs/coyote-parity.md). The
  cross-assembly uncontrolled-task detection pass
  (`CrossAssemblyTaskDetectionPass.cs`) and the controlled `Thread`, `Task.Run`,
  `TaskFactory`, `ThreadPool`, and `Parallel` runtime surfaces are original Clockwork
  code informed by Coyote's model but not copied from its source.

- **Adapted monitor/lock/semaphore material:** Clockwork's controlled `Monitor`, `System.Threading.Lock`,
  and `SemaphoreSlim` runtime surfaces
  (`src/Orleans.Testing.Simulation/Runtime/Threading/ControlledMonitor.cs`, `ControlledLock.cs`,
  `ControlledSemaphoreSlim.cs`) are a **design-level adaptation** of Coyote's controlled
  synchronization model — its `Microsoft.Coyote.Rewriting.Types.Threading.Monitor` and
  `SemaphoreSlim` wrappers and their tests informed the surface (ownership/recursion counts,
  the atomic wait/pulse condition-variable protocol, and max-count enforcement). **No Coyote
  source was copied verbatim into these files:** unlike Coyote's scheduler-backed wrappers,
  Clockwork's primitives are built on the cooperative logical-thread coordinator
  (`SimulationTaskRuntime`) with an ambient logical-strand identity, model "blocking" as loop
  pumping via `DrainUntil`, and associate per-object state through a weak-keyed
  `ConditionalWeakTable`. Because the approach — not literal source — was adapted, these files
  do not carry a Coyote copyright header; this entry records the design lineage. The per-member
  parity ledger (every Coyote `Monitor`/`SemaphoreSlim` entry classified controlled, rejected
  with a tested reason, or non-applicable on .NET 11) is enumerated in
  [docs/coyote-parity.md](docs/coyote-parity.md); the exact controlled/rejected signature list
  is in [docs/rule-inventory.md](docs/rule-inventory.md).

- **Adapted wait-handle/atomic/spin material:** Clockwork's controlled event / wait-handle, atomic, and spin
  surfaces (`src/Orleans.Testing.Simulation/Runtime/Threading/ControlledWaitHandle.cs`,
  `ControlledEventWaitHandle.cs`, `ControlledRegisteredWaitHandle.cs`, `ControlledInterlocked.cs`,
  `ControlledVolatile.cs`, `ControlledSpinWait.cs`) are a **design-level adaptation** of Coyote's
  controlled synchronization model — its `Microsoft.Coyote.Rewriting.Types.Threading`
  `AutoResetEvent`/`ManualResetEvent`/`EventWaitHandle`/`WaitHandle`, `Interlocked`, `Volatile`, and
  `SpinWait` wrappers and their tests informed the surface (auto- vs manual-reset consumption, the
  multi-handle `WaitAny`/`WaitAll`/`SignalAndWait` registration protocol, indivisible atomic
  read-modify-write, acquire/release volatile intent, and cooperative spin yielding). **No Coyote
  source was copied verbatim into these files:** as with the other controlled synchronization
  primitives, Clockwork's implementations are built on
  the cooperative logical-thread coordinator (`SimulationTaskRuntime`) with weak-keyed
  `ConditionalWeakTable` side state and virtual-time deadlines rather than Coyote's scheduler-backed
  wrappers, so they carry no Coyote copyright header; this entry records the design lineage. The
  per-member parity ledger (every Coyote event/`WaitHandle`/`Interlocked`/`Volatile`/`SpinWait` entry
  classified controlled, rejected with a tested reason, or non-applicable on .NET 11) is enumerated in
  [docs/coyote-parity.md](docs/coyote-parity.md); the exact controlled/rejected signature list is in
  [docs/rule-inventory.md](docs/rule-inventory.md).

- **Adapted race-exploration material:** `RaceExplorationRewritingPass.cs` adapts instruction
  selection, volatile-prefix placement, and constructor/property exclusions from Coyote
  `Source/Test/Rewriting/Passes/Rewriting/MemoryAccessRewritingPass.cs` at commit
  `f2c135d201341ee5eff3d82cac62bdb85b25139f`. Clockwork adds static fields, vector arrays,
  indirect-access classification, stack spilling, exact source/IL metadata, state-machine handling,
  exception/branch boundary repair, and manifest records. `RaceInstrumentation.cs` and the
  scheduler integration are design-level adaptations of Coyote
  `Source/Core/Runtime/Scheduling/SchedulingPoint.cs`. `CollectionAccessRewritingPass.cs` is a
  design-level adaptation of Coyote's collection type-rewriting inventory and collection interception
  types under `Source/Test/Rewriting/Types/Collections`; Clockwork preserves the original BCL types
  instead of copying Coyote's wrapper subclasses. Race tests are original tests informed by Coyote's
  `Tests/Tests.BugFinding/DataRaceChecking` coverage.

Microsoft Coyote license:

> Copyright (c) Microsoft Corporation.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

### Mono.Cecil

- **License:** MIT
- **Repository:** https://github.com/jbevain/cecil
- **Distribution:** `Microsoft.Orleans.Testing.Simulation.Instrumentation.Build` and
  `Microsoft.Orleans.Testing.Simulation.Tool` bundle the Mono.Cecil 0.11.6 binaries so
  their build task and tool can run without exposing Mono.Cecil as a package dependency.
  `Microsoft.Orleans.Testing.Simulation.Instrumentation` references Mono.Cecil as a normal
  package dependency. These packages include this notice and the complete Mono.Cecil MIT
  copyright/license text below. Mono.Cecil is not referenced by the simulation kernel.
- **Adaptation policy:** Mono.Cecil is used through its public API; no Mono.Cecil source
  has been copied or adapted into this repository. If source is copied or adapted in
  the future, the same attribution requirements as Coyote above apply.

Mono.Cecil license:

> Copyright (c) 2008 - 2015 Jb Evain
>
> Copyright (c) 2008 - 2011 Novell, Inc.
>
> Permission is hereby granted, free of charge, to any person obtaining
> a copy of this software and associated documentation files (the
> "Software"), to deal in the Software without restriction, including
> without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to
> the following conditions:
>
> The above copyright notice and this permission notice shall be
> included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
> EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
> MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
> LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
> OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
> WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### FoundationDB

- **License:** Apache License 2.0
- **Repository:** https://github.com/apple/foundationdb
- **Why it's relevant:** FoundationDB's deterministic simulation testing approach
  (a simulated network/clock driving the same binary used in production) is a
  direct architectural precedent for Clockwork's kernel and supported
  instrumentation modes.
- **Adaptation policy:** Apache-2.0 requires: retaining copyright/license/NOTICE
  text, stating any changes made to adapted files, and including a copy of the
  Apache-2.0 license for any adapted files. Because Apache-2.0 and MIT are not
  compatible in the same direction for combined works in all cases (Apache-2.0 code
  can be included with attribution, but relicensing Apache-2.0 code under this
  repository's MIT license is not permitted), any FoundationDB-derived source or
  test must be kept identifiably separate (clearly marked file header and license
  pointer) rather than silently merged into MIT-licensed files in this repository.
  Where practical, prefer reimplementing the *approach* (which is not copyrightable)
  over adapting FoundationDB's literal source.

## General policy

- Clockwork uses the `Mono.Cecil` (0.11.6, MIT) package dependency in
  `Orleans.Testing.Simulation.Instrumentation`, and the `Microsoft.CodeAnalysis.CSharp` (Roslyn, MIT)
  package to `Clockwork.Instrumentation.Tests` (used only to compile fixture
  assemblies at test time). It also adapts two Coyote source files as recorded above.
  The runtime and simulation projects remain free of any IL-rewriting dependency.
- Before adapting source from any of the projects above, or from any other
  third-party project, update this document in the same pull request as the
  adaptation.
- Clockwork itself is licensed under the [MIT License](LICENSE); nothing in this
  document changes that for Clockwork's own original code.
