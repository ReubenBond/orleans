namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// What one sweep found among the distinct run objects its listing named, and what it did (archive §2.7, §8.5).
/// </summary>
/// <param name="Referenced">The objects the manifest read after the listing references.</param>
/// <param name="Pending">The unreferenced objects whose reference condition holds, which a commit could still reference:
/// uploads in flight, and changes not yet decided.</param>
/// <param name="Marked">The unreferenced objects whose reference condition this sweep found false, and no earlier sweep
/// of this sweeper had: a later sweep deletes each which it finds deletable too.</param>
/// <param name="Deleted">The objects this sweep deleted: each unreferenced with its condition false, as an earlier sweep
/// of this sweeper had found it too.</param>
/// <param name="Unreadable">The unreferenced objects whose reference condition the sweep could not read, which it left in
/// place and logged.</param>
public readonly record struct SweepResult(int Referenced, int Pending, int Marked, int Deleted, int Unreadable);
