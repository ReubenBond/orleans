using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests.LazyFs;

/// <summary>
/// A cross-process Orleans <see cref="IMembershipTable"/> backed by a single <see cref="FileSystemMetaStore"/>
/// conditional register, allowing any silo (or every silo simultaneously) to be <c>SIGKILL</c>'d and restarted
/// without depending on a primary membership silo.
/// </summary>
internal sealed class FileSystemMembershipTable : IMembershipTable
{
    private const string RegisterName = "orleans-membership";
    private const int MaxCasRetries = 32;

    private readonly IMetaStore _metaStore;
    private readonly Guid _cluster;
    private IConditionalRegister? _register;

    public FileSystemMembershipTable(IMetaStore metaStore, Guid cluster)
    {
        ArgumentNullException.ThrowIfNull(metaStore);
        _metaStore = metaStore;
        _cluster = cluster;
    }

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        _register = await _metaStore.GetRegisterAsync(_cluster, RegisterName).ConfigureAwait(false);
        if (!tryInitTableVersion)
        {
            return;
        }

        using var existing = await _register.ReadAsync().ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        var initial = Serialize(new StoredTable(0, NextEtag(), []));
        _ = await _register.WriteAsync(null, new ReadOnlySequence<byte>(initial)).ConfigureAwait(false);
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            using var entry = await register.ReadAsync().ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            var current = Deserialize(entry);
            var cleared = Serialize(new StoredTable(current.Version + 1, NextEtag(), []));
            var outcome = await register.WriteAsync(entry.Version, new ReadOnlySequence<byte>(cleared)).ConfigureAwait(false);
            if (outcome is RegisterWriteOutcome.Applied)
            {
                return;
            }
        }
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        var cutoffUtc = beforeDate.UtcDateTime;
        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            using var entry = await register.ReadAsync().ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            var current = Deserialize(entry);
            var retained = current.Entries
                .Where(row => row.Status != (int)SiloStatus.Dead || row.IAmAliveTimeUtc >= cutoffUtc)
                .ToList();
            if (retained.Count == current.Entries.Count)
            {
                return;
            }

            var next = Serialize(new StoredTable(current.Version + 1, NextEtag(), retained));
            var outcome = await register.WriteAsync(entry.Version, new ReadOnlySequence<byte>(next)).ConfigureAwait(false);
            if (outcome is RegisterWriteOutcome.Applied)
            {
                return;
            }
        }
    }

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        using var entry = await register.ReadAsync().ConfigureAwait(false);
        if (entry is null)
        {
            return new MembershipTableData(new TableVersion(0, "0"));
        }

        var table = Deserialize(entry);
        var version = new TableVersion(table.Version, table.TableEtag);
        var keyText = key.ToParsableString();
        var match = table.Entries.FirstOrDefault(row => string.Equals(row.SiloAddress, keyText, StringComparison.Ordinal));
        return match is null
            ? new MembershipTableData(version)
            : new MembershipTableData(Tuple.Create(ToMembershipEntry(match), match.Etag), version);
    }

    public async Task<MembershipTableData> ReadAll()
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        using var entry = await register.ReadAsync().ConfigureAwait(false);
        if (entry is null)
        {
            return new MembershipTableData(new TableVersion(0, "0"));
        }

        var table = Deserialize(entry);
        var version = new TableVersion(table.Version, table.TableEtag);
        var rows = table.Entries
            .Select(row => Tuple.Create(ToMembershipEntry(row), row.Etag))
            .ToList();
        return new MembershipTableData(rows, version);
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        var keyText = entry.SiloAddress.ToParsableString();
        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            using var currentEntry = await register.ReadAsync().ConfigureAwait(false);
            if (currentEntry is null)
            {
                return false;
            }

            var table = Deserialize(currentEntry);
            if (!string.Equals(table.TableEtag, tableVersion.VersionEtag, StringComparison.Ordinal))
            {
                return false;
            }

            if (table.Entries.Any(row => string.Equals(row.SiloAddress, keyText, StringComparison.Ordinal)))
            {
                return false;
            }

            List<StoredEntry> updated = [.. table.Entries, FromMembershipEntry(entry, NextEtag())];

            var payload = Serialize(new StoredTable(tableVersion.Version, NextEtag(), updated));
            var outcome = await register.WriteAsync(currentEntry.Version, new ReadOnlySequence<byte>(payload)).ConfigureAwait(false);
            if (outcome is RegisterWriteOutcome.Applied)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        var keyText = entry.SiloAddress.ToParsableString();
        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            using var currentEntry = await register.ReadAsync().ConfigureAwait(false);
            if (currentEntry is null)
            {
                return false;
            }

            var table = Deserialize(currentEntry);
            if (!string.Equals(table.TableEtag, tableVersion.VersionEtag, StringComparison.Ordinal))
            {
                return false;
            }

            var index = table.Entries.FindIndex(row => string.Equals(row.SiloAddress, keyText, StringComparison.Ordinal));
            if (index < 0 || !string.Equals(table.Entries[index].Etag, etag, StringComparison.Ordinal))
            {
                return false;
            }

            List<StoredEntry> updated = [.. table.Entries];
            updated[index] = FromMembershipEntry(entry, NextEtag());

            var payload = Serialize(new StoredTable(tableVersion.Version, NextEtag(), updated));
            var outcome = await register.WriteAsync(currentEntry.Version, new ReadOnlySequence<byte>(payload)).ConfigureAwait(false);
            if (outcome is RegisterWriteOutcome.Applied)
            {
                return true;
            }
        }

        return false;
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        var register = await GetRegisterAsync().ConfigureAwait(false);
        var keyText = entry.SiloAddress.ToParsableString();
        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            using var currentEntry = await register.ReadAsync().ConfigureAwait(false);
            if (currentEntry is null)
            {
                return;
            }

            var table = Deserialize(currentEntry);
            var index = table.Entries.FindIndex(row => string.Equals(row.SiloAddress, keyText, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var existing = table.Entries[index];
            List<StoredEntry> updated = [.. table.Entries];
            updated[index] = existing with { IAmAliveTimeUtc = entry.IAmAliveTime.ToUniversalTime() };

            // Keep tableVersion, tableEtag, and row ETag intact as Orleans' UpdateIAmAlive contract expects.
            var payload = Serialize(new StoredTable(table.Version, table.TableEtag, updated));
            var outcome = await register.WriteAsync(currentEntry.Version, new ReadOnlySequence<byte>(payload)).ConfigureAwait(false);
            if (outcome is RegisterWriteOutcome.Applied)
            {
                return;
            }
        }
    }

    private async ValueTask<IConditionalRegister> GetRegisterAsync() =>
        _register ??= await _metaStore.GetRegisterAsync(_cluster, RegisterName).ConfigureAwait(false);

    private static string NextEtag() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static byte[] Serialize(StoredTable table) => JsonSerializer.SerializeToUtf8Bytes(table);

    private static StoredTable Deserialize(MetaStoreEntry entry) =>
        JsonSerializer.Deserialize<StoredTable>(entry.Value.ToArray())
        ?? throw new InvalidDataException("The stored Orleans membership table is null.");

    private static StoredEntry FromMembershipEntry(MembershipEntry entry, string etag) => new(
        entry.SiloAddress.ToParsableString(),
        (int)entry.Status,
        entry.ProxyPort,
        entry.HostName,
        entry.SiloName,
        entry.RoleName ?? string.Empty,
        entry.UpdateZone,
        entry.FaultZone,
        entry.StartTime.ToUniversalTime(),
        entry.IAmAliveTime.ToUniversalTime(),
        etag,
        entry.SuspectTimes is { Count: > 0 } suspects
            ? [.. suspects.Select(static suspect => new StoredSuspect(suspect.Item1.ToParsableString(), suspect.Item2.ToUniversalTime()))]
            : []);

    private static MembershipEntry ToMembershipEntry(StoredEntry row)
    {
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.FromParsableString(row.SiloAddress),
            Status = (SiloStatus)row.Status,
            ProxyPort = row.ProxyPort,
            HostName = row.HostName,
            SiloName = row.SiloName,
            RoleName = row.RoleName,
            UpdateZone = row.UpdateZone,
            FaultZone = row.FaultZone,
            StartTime = DateTime.SpecifyKind(row.StartTimeUtc, DateTimeKind.Utc),
            IAmAliveTime = DateTime.SpecifyKind(row.IAmAliveTimeUtc, DateTimeKind.Utc),
        };
        if (row.SuspectTimes.Count > 0)
        {
            entry.SuspectTimes =
                [.. row.SuspectTimes.Select(static suspect => Tuple.Create(SiloAddress.FromParsableString(suspect.SiloAddress), DateTime.SpecifyKind(suspect.TimeUtc, DateTimeKind.Utc)))];
        }

        return entry;
    }

    private sealed record StoredTable(int Version, string TableEtag, List<StoredEntry> Entries);

    private sealed record StoredEntry(
        string SiloAddress,
        int Status,
        int ProxyPort,
        string HostName,
        string SiloName,
        string RoleName,
        int UpdateZone,
        int FaultZone,
        DateTime StartTimeUtc,
        DateTime IAmAliveTimeUtc,
        string Etag,
        List<StoredSuspect> SuspectTimes);

    private sealed record StoredSuspect(string SiloAddress, DateTime TimeUtc);
}
