// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The verdict of a linearizability check.
/// </summary>
/// <remarks>
/// Checking linearizability is NP-complete, so a check may be cancelled, for example by a timeout. A cancelled check
/// which has found no illegal partition is <see cref="Unknown"/>.
/// </remarks>
internal enum LinearizabilityVerdict
{
    /// <summary>Every partition has a linearization.</summary>
    Ok,

    /// <summary>Some partition has no linearization.</summary>
    Illegal,

    /// <summary>The check was cancelled before it found an illegal partition or linearized every partition.</summary>
    Unknown,
}
