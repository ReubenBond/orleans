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
    private static GrainAddress InvalidGrainId = new();
    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration) 
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RegisterAsync('{Version}', '{Address}', '{ExistingAddress}')", version, address, currentRegistration);
        }

        // Ensure that the current membership version is new enough.
        await WaitForRange(address.GrainId, version);
        if (!IsOwner(_view, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress>(_view.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return DirectoryResult.FromResult(RegisterCore(address, currentRegistration), version);
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
            if (!IsOwner(_view, address.GrainId))
            {
                return DirectoryResult.RefreshRequired<List<GrainAddress>>(_view.Version);
            }

            DebugAssertOwnership(address.GrainId);
            results.Add(RegisterCore(address, null));
        }

        return DirectoryResult.FromResult(results, version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("LookupAsync('{Version}', '{GrainId}')", version, grainId);
        }

        // Ensure we can serve the request.
        await WaitForRange(grainId, version);
        if (!IsOwner(_view, grainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress?>(_view.Version);
        }

        return DirectoryResult.FromResult(LookupCore(grainId), version);
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
            if (!IsOwner(_view, grainId))
            {
                return DirectoryResult.RefreshRequired<List<GrainAddress?>>(_view.Version);
            }

            DebugAssertOwnership(grainId);
            results.Add(LookupCore(grainId));
        }

        return DirectoryResult.FromResult(results, version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("DeregisterAsync('{Version}', '{Address}')", version, address);
        }

        await WaitForRange(address.GrainId, version);
        if (!IsOwner(_view, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<bool>(_view.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return DirectoryResult.FromResult(DeregisterCore(address), version);
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
            if (!IsOwner(_view, address.GrainId))
            {
                return DirectoryResult.RefreshRequired<bool>(_view.Version);
            }

            DebugAssertOwnership(address.GrainId);
            result &= DeregisterCore(address);
        }

        return DirectoryResult.FromResult(result, version);
    }

    async ValueTask<BulkDirectoryResponse> IGrainDirectoryReplica.ApplyBulk(BulkDirectoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = request.Version;
        await RefreshViewAsync(version, CancellationToken.None);

        var response = new BulkDirectoryResponse
        {
            Version = _view.Version
        };

        // Lookups
        var lookupRequests = request.Lookups;
        if (lookupRequests is not null)
        {
            var results = new List<GrainAddress?>(lookupRequests.Count);
            foreach (var grainId in lookupRequests)
            {
                await WaitForRange(grainId, version);
                if (!IsOwner(_view, grainId))
                {
                    // This replica is unable to serve the request.
                    results.Add(InvalidGrainId);
                    continue;
                }

                DebugAssertOwnership(grainId);
                results.Add(LookupCore(grainId));
            }

            response.Lookups = results;
        }

        var registrationRequests = request.Registrations;
        if (registrationRequests is not null)
        {
            var results = new List<GrainAddress?>(registrationRequests.Count);
            foreach (var (newAddress, existingAddress) in registrationRequests)
            {
                await WaitForRange(newAddress.GrainId, version);
                if (!IsOwner(_view, newAddress.GrainId))
                {
                    // This replica is unable to serve the request.
                    results.Add(null);
                    continue;
                }

                DebugAssertOwnership(newAddress.GrainId);
                results.Add(RegisterCore(newAddress, existingAddress));
            }

            response.Registrations = results;
        }

        var deregistrationRequests = request.Deregistrations;
        if (deregistrationRequests is not null)
        {
            var results = new List<bool?>(deregistrationRequests.Count);
            foreach (var address in deregistrationRequests)
            {
                await WaitForRange(address.GrainId, version);
                if (!IsOwner(_view, address.GrainId))
                {
                    // This replica is unable to serve the request.
                    results.Add(null);
                    continue;
                }

                DebugAssertOwnership(address.GrainId);
                results.Add(DeregisterCore(address));
            }

            response.Deregistrations = results;
        }

        return response;
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
}
