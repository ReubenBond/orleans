# Code craft

- **Push `if`s up, `for`s down.** Callers decide which case applies and pass helpers the whole batch; the coordinating method owns state mutation while the helpers it calls compute results without side effects ([matklad](https://github.com/ReubenBond/research-papers/blob/main/Push_Ifs_Up_and_Fors_Down/Push_Ifs_Up_and_Fors_Down.md)).
- **Functions fit on a screen.** A function reads as one unit; one beyond about 70 lines prompts a check for mixed responsibilities.
- **Smallest scope.** Values are computed close to their use, so nothing can invalidate a check between the check and the use; each piece of mutable state has one owner and one copy.
- **Simple signatures.** Each extra return dimension (nullability, error result, async) adds branches to every caller, so a method returns the simplest shape its contract allows (`void` over `bool` over `int` over `int?`).
- **Positive conditions.** Conditions read in positive form (`index < count`, not `!(index >= count)`); compound conditions whose terms need different handling are split, and each branch is handled deliberately.
- **Explicit options.** Behavior-critical choices are visible at the call site or the configuration boundary that owns them.
- **Precise names.** Names are precise nouns and verbs with one meaning each; abbreviations are limited to established terms (ID, ETag, GC, RPC).
- **Units in types.** Durations use `TimeSpan`, instants use `DateTimeOffset` in new APIs and the established type in existing contracts, and raw numbers carry the unit in the name (`timeoutMs`).
- **Grouped names.** In new code, related names share a prefix so they sort together (`LatencyMax`, `LatencyMin`), unless local conventions establish another pattern.
- **Compiler strictness.** Code builds cleanly under warnings-as-errors and the repository's nullable settings; each null-forgiving operator or suppression relies on an invariant that is evident nearby or stated in a comment.
- **Documentation.** Non-obvious or changed contracts are documented near the code in comments or XML API docs; broader design context goes in architecture documentation. Documentation matches the affected behavior, audience, and document type, and describes affirmative behavior and outcomes.

Worked guidance: [trusted input and explicit error states](applied-principles.md#make-error-paths-preserve-the-state-machine) and [batch-level decisions](applied-principles.md#budget-fan-out-and-speculative-work).
