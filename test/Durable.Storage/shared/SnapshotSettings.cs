using System.Globalization;
using System.Runtime.CompilerServices;
using DiffEngine;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The snapshot conventions of every test project which references Verify (verification.md §8). A snapshot lives
/// beside its test class as <c>{Class}.{Method}.verified.txt</c>.
/// </summary>
/// <remarks>
/// Verify scrubs the solution, project, output, temporary, and user-profile directories, and the GUID and date members
/// of the objects it serializes. These settings also scrub GUIDs in their dashed form and ISO 8601 round-trip times
/// within text. GUIDs in the 32-digit form stay, since that form is also how renderings show bytes and pinned UUIDs.
/// A mismatch writes a <c>.received</c> file beside the snapshot, and its review happens in the change's diff, so no
/// diff tool is launched.
/// </remarks>
internal static class SnapshotSettings
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        DiffRunner.Disabled = true;
        VerifierSettings.ScrubInlineGuids(GuidFormats.Dashed);
        VerifierSettings.ScrubInlineDateTimes("O", CultureInfo.InvariantCulture);
        VerifierSettings.ScrubInlineDateTimeOffsets("O", CultureInfo.InvariantCulture);
    }
}
