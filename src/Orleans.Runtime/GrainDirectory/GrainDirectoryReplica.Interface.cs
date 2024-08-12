using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryReplica
{
    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration) 
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RegisterAsync('{Version}', '{Address}', '{ExistingAddress}')", version, address, currentRegistration);
        }

        // Ensure that the current membership version is new enough.
        await WaitForRange(address.GrainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<GrainAddress>(null!, _view.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return new DirectoryResult<GrainAddress>(RegisterCore(address, currentRegistration), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress>>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, List<GrainAddress> addresses) 
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RegisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);
        }

        var results = new List<GrainAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            await WaitForRange(address.GrainId, version);
            if (!IsExpectedView(version))
            {
                return new DirectoryResult<List<GrainAddress>>(null!, _view.Version);
            }

            DebugAssertOwnership(address.GrainId);
            results.Add(RegisterCore(address, null));
        }

        return new DirectoryResult<List<GrainAddress>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("LookupAsync('{Version}', '{GrainId}')", version, grainId);
        }

        // Ensure we can serve the request.
        await WaitForRange(grainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<GrainAddress?>(null, _view.Version);
        }

        return new DirectoryResult<GrainAddress?>(LookupCore(grainId), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress?>>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, List<GrainId> grainIds)
    {
        ArgumentNullException.ThrowIfNull(grainIds);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("LookupAsync('{Version}', '{GrainIdCount}')", version, grainIds.Count);
        }

        var results = new List<GrainAddress?>(grainIds.Count);
        foreach (var grainId in grainIds)
        {
            await WaitForRange(grainId, version);
            if (IsOwner(_view, grainId))
            {
                DebugAssertOwnership(grainId);
                results.Add(LookupCore(grainId));
            }
            else if (!IsExpectedView(version))
            {
                return new DirectoryResult<List<GrainAddress?>>(null!, _view.Version);
            }
        }

        return new DirectoryResult<List<GrainAddress?>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("DeregisterAsync('{Version}', '{Address}')", version, address);
        }

        await WaitForRange(address.GrainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<bool>(false, _view.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return new DirectoryResult<bool>(DeregisterCore(address), _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.DeregisterAsync(MembershipVersion version, List<GrainAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("DeregisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);
        }

        var result = true;
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            await WaitForRange(address.GrainId, version);
            if (!IsExpectedView(version))
            {
                return new DirectoryResult<bool>(false, _view.Version);
            }

            DebugAssertOwnership(address.GrainId);
            result &= DeregisterCore(address);
        }

        return new DirectoryResult<bool>(result, _view.Version);
    }

    private bool DeregisterCore(GrainAddress address)
    {
        if (_directory.TryGetValue(address.GrainId, out var existing) && (existing.Matches(address) || IsSiloDead(existing)))
        {
            return _directory.Remove(address.GrainId);
        }

        return false;
    }

    private GrainAddress? LookupCore(GrainId grainId)
    {
        if (_directory.TryGetValue(grainId, out var existing) && !IsSiloDead(existing))
        {
            return existing;
        }

        return null;
    }

    private GrainAddress RegisterCore(GrainAddress newAddress, GrainAddress? existingAddress)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(_directory, newAddress.GrainId, out _);

        if (existing is null || existing.Matches(existingAddress) || IsSiloDead(existing))
        {
            if (newAddress.MembershipVersion != _view.Version)
            {
                // Set the membership version to match the view number in which it was registered.
                newAddress = new()
                {
                    GrainId = newAddress.GrainId,
                    SiloAddress = newAddress.SiloAddress,
                    ActivationId = newAddress.ActivationId,
                    MembershipVersion = _view.Version
                };
            }

            existing = newAddress;
        }

        return existing;
    }

    private bool IsSiloDead(GrainAddress existing) => _clusterMembershipService.CurrentSnapshot.GetSiloStatus(existing.SiloAddress) == SiloStatus.Dead;
    private bool IsExpectedView(MembershipVersion version) => version == _view.Version ||  _stoppedCts.IsCancellationRequested;
}
