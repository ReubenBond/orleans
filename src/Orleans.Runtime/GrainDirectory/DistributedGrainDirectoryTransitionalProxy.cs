using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DistributedGrainDirectoryTransitionalProxy : IRemoteGrainDirectory
{
    private readonly DistributedGrainDirectory _directory;
    public DistributedGrainDirectoryTransitionalProxy(DistributedGrainDirectory directory)
    {

    }

    Task IRemoteGrainDirectory.AcceptSplitPartition(List<GrainAddress> singleActivations) => Task.CompletedTask;
    async Task IDhtGrainDirectory.DeleteGrainAsync(GrainId grainId, int hopCount)
    {
        await _directory.Unregister(GrainAddress.GetAddress(null, grainId, default));
    }

    async Task<AddressAndTag> IDhtGrainDirectory.LookupAsync(GrainId grainId, int hopCount)
    {
        var result = await _directory.Lookup(grainId);
        return new AddressAndTag(result, 0);
    }

    async Task<List<AddressAndTag>> IRemoteGrainDirectory.LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        var results = new List<AddressAndTag>(grainAndETagList.Count);
        var tasks = new List<Task<GrainAddress?>>(grainAndETagList.Count);
        foreach (var (grainId, version) in grainAndETagList)
        {
            tasks.Add(_directory.Lookup(grainId));
        }
        await Task.WhenAll(tasks);
        for (var i = 0; i < grainAndETagList.Count; i++)
        {
            var address = await tasks[i];
            results.Add(new AddressAndTag(address, grainAndETagList[i].Version));
        }

        return results;
    }

    async Task<AddressAndTag> IDhtGrainDirectory.RegisterAsync(GrainAddress address, int hopCount)
    {
        var result = await _directory.Register(address);
        return new(result, 0);
    }

    async Task<AddressAndTag> IDhtGrainDirectory.RegisterAsync(GrainAddress address, GrainAddress? currentRegistration, int hopCount)
    {
        var result = await _directory.Register(address, currentRegistration);
        return new(result, 0);
    }

    async Task IRemoteGrainDirectory.RegisterMany(List<GrainAddress> addresses)
    {
        var tasks = new List<Task>(addresses.Count);
        foreach (var address in addresses)
        {
            tasks.Add(_directory.Register(address));
        }
        await Task.WhenAll(tasks);
    }

    async Task IDhtGrainDirectory.UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
    {
        await _directory.Unregister(address);
    }

    async Task IDhtGrainDirectory.UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
    {
        var tasks = new List<Task>(addresses.Count);
        foreach (var address in addresses)
        {
            tasks.Add(_directory.Unregister(address));
        }

        await Task.WhenAll(tasks);
    }
}
