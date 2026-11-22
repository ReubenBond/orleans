; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------
CW1001  | Clockwork.Determinism | Info | NondeterministicApiAnalyzer, controlled/rejected BCL, task, thread, thread-pool, Parallel, PLINQ, channel (`ReadAllAsync`, `ReadAsync`, `WriteAsync`, except a virtual call whose receiver's type overrides the member), BlockingCollection, Stream/TextReader/TextWriter async, BackgroundWorker/BackgroundService start, TPL Dataflow, WebSocket-over-stream factories, Monitor, Lock, and SemaphoreSlim surface
CW1003  | Clockwork.Determinism | Warning | Position-sensitive Dictionary/HashSet enumeration consumers
CW1004  | Clockwork.Runtime | Error | CompletionAuthorityAnalyzer, a completion member called, or made a delegate, in the Clockwork runtime assembly outside SimulationCompletion, or a [CompletionCallback] delegate passed elsewhere
