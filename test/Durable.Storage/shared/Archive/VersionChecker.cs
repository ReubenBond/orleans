using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The version checker (verification.md §3.2): a key's versions never repeat; the version bases its store's reads answer
/// for a rebase, with a map value which lists no ledger holding a row of the key, never repeat, but for a key the LSM has
/// forgotten entirely (archive.md §8.3); every version base any read answers names one state of the key, so a rebase
/// to it is fresh (HI-I4); every version a journal's answers report names one state, which may have several names, and a
/// rebase takes the store's version base (JS-I10). Once the LSM forgets a key entirely, after an accepted
/// <c>Expired</c>, the key's new lifetime may repeat its <c>Origin</c>-based versions, a bounded exception the caller
/// declares through <see cref="Forget"/>.
/// </summary>
internal sealed class VersionChecker
{
    private readonly Dictionary<string, HashSet<RowVersion>> _versions = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, HashSet<RowToken>> _bases = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(string Key, RowToken Base), string> _states = [];
    private readonly Dictionary<(string Key, RowVersion Version), string> _named = [];

    /// <summary>
    /// Records that an answer for a key, a read's, a write's, or a rejection's, named <paramref name="state"/>, which the
    /// caller identifies exactly, by <paramref name="version"/>; returns the violation, in words, when an earlier answer
    /// named another state by it: a state may have several names, but no version names two states (JS-I10).
    /// </summary>
    public string? Named(ReadOnlySpan<byte> key, RowVersion version, string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var name = (Convert.ToHexString(key), version);
        if (_named.TryGetValue(name, out var earlier))
        {
            return string.Equals(earlier, state, StringComparison.Ordinal)
                ? null
                : Invariant($"the version {version} of key {Convert.ToHexStringLower(key)} names the state {state}, and earlier {earlier}");
        }

        _named.Add(name, state);
        return null;
    }

    /// <summary>
    /// Records that an answer for a key rebased <paramref name="state"/> to <paramref name="versionBase"/>, which must be
    /// <paramref name="storeBase"/>, the store's version base for the key when every row of it had dropped (journals.md
    /// §8.3); returns the violation, in words, when the base is another, or names another state.
    /// </summary>
    public string? Rebased(ReadOnlySpan<byte> key, RowToken versionBase, RowToken storeBase, string state) =>
        versionBase != storeBase
            ? Invariant($"the rebase of key {Convert.ToHexStringLower(key)} to {versionBase}, not the store's version base {storeBase}")
            : State(key, versionBase, state);

    /// <summary>
    /// Forgets the key's <c>Origin</c>-based versions and their names, as the LSM forgets a key entirely once an accepted
    /// <c>Expired</c> has been compacted away, so its new lifetime may repeat its earlier one's <c>Origin</c>-based
    /// versions (JS-I10); a version of any other base stays the key's alone.
    /// </summary>
    public void Forget(ReadOnlySpan<byte> key)
    {
        var name = Convert.ToHexString(key);
        if (_versions.TryGetValue(name, out var versions))
        {
            _ = versions.RemoveWhere(static version => version.Base.IsEmpty);
        }

        if (_bases.TryGetValue(name, out var bases))
        {
            _ = bases.Remove(default);
        }

        _ = _states.Remove((name, default));
        foreach (var named in _named.Keys.Where(named => named.Key == name && named.Version.Base.IsEmpty).ToArray())
        {
            _ = _named.Remove(named);
        }
    }

    /// <summary>Records a row's version; returns the violation, in words, when the key's versions repeat it.</summary>
    public string? Version(ReadOnlySpan<byte> key, RowVersion version) =>
        Set(_versions, key).Add(version) ? null : Invariant($"the version {version} of key {Convert.ToHexStringLower(key)} repeats");

    /// <summary>
    /// Records the version base a rebase read of a key answered, with <paramref name="state"/>, after the store applied
    /// another write of it; returns the violation, in words, when an earlier rebase read answered it (HI-I4's freshness)
    /// or it names another state.
    /// </summary>
    public string? VersionBase(ReadOnlySpan<byte> key, RowToken versionBase, string state) =>
        Set(_bases, key).Add(versionBase)
            ? State(key, versionBase, state)
            : Invariant($"the version base {versionBase} of key {Convert.ToHexStringLower(key)} repeats while its store holds it");

    /// <summary>
    /// Records that a read of a key answered <paramref name="versionBase"/> with <paramref name="state"/>; returns the
    /// violation, in words, when an earlier read answered it with another state, so a rebase to it would not be fresh.
    /// </summary>
    public string? State(ReadOnlySpan<byte> key, RowToken versionBase, string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var name = (Convert.ToHexString(key), versionBase);
        if (_states.TryGetValue(name, out var earlier))
        {
            return string.Equals(earlier, state, StringComparison.Ordinal)
                ? null
                : Invariant($"the version base {versionBase} of key {Convert.ToHexStringLower(key)} names the state {state}, and earlier {earlier}");
        }

        _states.Add(name, state);
        return null;
    }

    private static HashSet<T> Set<T>(Dictionary<string, HashSet<T>> sets, ReadOnlySpan<byte> key)
    {
        var name = Convert.ToHexString(key);
        if (!sets.TryGetValue(name, out var set))
        {
            sets.Add(name, set = []);
        }

        return set;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
