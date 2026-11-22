using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The incremental fold of a provider's <c>Plan</c> (archive.md §3, §4): <see cref="IArchiveStore.Begin"/> starts it at
/// a write's token, <see cref="Add"/> takes each command in order, and <see cref="End"/> answers the key's token after
/// them, as <see cref="ArchiveStorePlans.Plan"/> answers the whole write. Its state, the provider's, is constant in the
/// write's length, so admission keeps one fold per key's write and asks <see cref="Admits"/> before each command, and a
/// drain replaying the same commands reaches the same token (HI-I3). It is the one definition of <c>Plan</c> and
/// <c>Admits</c>: no provider answers them otherwise.
/// </summary>
public abstract class ArchivePlan
{
    /// <summary>
    /// The most bytes a fold's state takes, which <see cref="IArchiveStore.Begin"/> allocates and no <see cref="Add"/> of
    /// an <c>Append</c> grows: every provider's fold stays within it, which its contract tests check, so admission counts a
    /// warm key's folds by it whatever the store (journals.md §8.8).
    /// </summary>
    public const int MaxStateBytes = 128;

    /// <summary>
    /// Returns the fold of a provider whose only token is empty (<c>Origin</c>), as the LSM's: its token stays empty for every
    /// write, and it admits every command.
    /// </summary>
    public static ArchivePlan Empty() => new EmptyPlan();

    /// <summary>Gets the number of commands the fold has taken.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets the number of the key's rows in the segment before the write's opener, which it supersedes; a provider whose
    /// tokens count them reads them when it applies the opener.
    /// </summary>
    public int Superseded { get; private set; }

    /// <summary>
    /// Takes <paramref name="rows"/> of the key's rows in the segment which the write's opener, its next command,
    /// supersedes (<see cref="ArchiveWrite.Superseded"/>), before any command.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The fold has taken a command.</exception>
    public void Supersede(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        if (Count > 0)
        {
            throw new InvalidOperationException("Rows are superseded only before the write's opener, its first command.");
        }

        Superseded = checked(Superseded + rows);
    }

    /// <summary>Takes the write's next command.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="command"/> opens a write which already holds a command, or is no opener after superseded rows.
    /// </exception>
    public void Add(ArchiveCommand command)
    {
        CheckOrder(command);
        Apply(command);
        Count++;
    }

    /// <summary>
    /// Returns whether the write may grow by <paramref name="command"/> within the provider's limits on what one write or
    /// one generation holds.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="command"/> opens a write which already holds a command.</exception>
    public bool Admits(ArchiveCommand command)
    {
        CheckOrder(command);
        return AdmitsNext(command);
    }

    /// <summary>Returns the key's token after the commands taken; with none, the token the fold began at.</summary>
    public abstract RowToken End();

    /// <summary>Applies the write's next command, whose order the fold checked, to the provider's state.</summary>
    protected abstract void Apply(ArchiveCommand command);

    /// <summary>Returns whether the provider's limits admit the write's next command, whose order the fold checked.</summary>
    protected abstract bool AdmitsNext(ArchiveCommand command);

    private void CheckOrder(ArchiveCommand command)
    {
        if (command.IsOpener && Count > 0)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Only a write's first command opens it; the write holds {Count} already."),
                nameof(command));
        }

        if (!command.IsOpener && Count == 0 && Superseded > 0)
        {
            throw new ArgumentException("Only an opener follows the rows it supersedes.", nameof(command));
        }
    }

    private sealed class EmptyPlan : ArchivePlan
    {
        public override RowToken End() => default;

        protected override void Apply(ArchiveCommand command)
        {
        }

        protected override bool AdmitsNext(ArchiveCommand command) => true;
    }
}

/// <summary>
/// <c>Plan</c> and <c>Admits</c> over a whole write (archive.md §3), each one pass of a store's <see cref="ArchivePlan"/>
/// fold, which defines them; neither is a member a provider could answer otherwise (HI-I3). Each costs O(write): for tests
/// and the reference path, such as a merge's chain check. Admission keeps its fold per key's write and asks
/// <see cref="ArchivePlan.Admits"/> before each command, so it never replays a write.
/// </summary>
public static class ArchiveStorePlans
{
    /// <summary>
    /// Returns the key's token after <paramref name="write"/>, from the token it carries; an empty write's token unchanged
    /// (HI-I3).
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The write's token is one the store could not have minted.</exception>
    public static RowToken Plan(this IArchiveStore store, ArchiveWrite write) => Fold(store, write).End();

    /// <summary>
    /// Returns whether <paramref name="write"/> may grow by <paramref name="command"/> within the store's limits on what
    /// one write or one generation holds.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The write's token is one the store could not have minted.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> opens a non-empty write.</exception>
    public static bool Admits(this IArchiveStore store, ArchiveWrite write, ArchiveCommand command) => Fold(store, write).Admits(command);

    private static ArchivePlan Fold(IArchiveStore store, ArchiveWrite write)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(write);
        var plan = store.Begin(write.Token);
        if (write.Superseded > 0)
        {
            plan.Supersede(write.Superseded);
        }

        foreach (var command in write.Commands)
        {
            plan.Add(command);
        }

        return plan;
    }
}
