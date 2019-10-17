<p align="center">
  <img src="https://raw.githubusercontent.com/dotnet/orleans/gh-pages/assets/logo_full.png" alt="Orleans logo" width="600px"> 
</p>

[![NuGet](https://img.shields.io/nuget/v/Microsoft.Orleans.Core.svg?style=flat)](http://www.nuget.org/profiles/Orleans)
[![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/dotnet/orleans?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge)

Orleans is a cross-platform framework for building robust, scalable distributed applications.

It builds on the developer productivity of .NET and brings it to the world of distributed applications such as cloud services. Orleans scales from a single on-premises server to globally distributed, highly-available applications in the cloud.

Orleans takes familiar concepts like objects, interfaces, async/await, and try/catch and extends them to multi-server environments. As such, it helps developers experienced with single-server applications transition to building resilient, scalable cloud services and other distributed applications. For this reason, Orleans has often been referred to as "Distributed .NET".

It was created by [Microsoft Research](http://research.microsoft.com/projects/orleans/) and introduced the [Virtual Actor Model](http://research.microsoft.com/apps/pubs/default.aspx?id=210931) as a novel approach to building a new generation of distributed systems for the Cloud era. The core contribution of Orleans is a programming model which tames the complexity inherent to highly-parallel distributed systems without restricting capabilities or imposing onerous constraints on the developer.

< insert grain diagram (gran = identity + behavior + state)>

The fundamental building block in any Orleans application is a *grain*. Grains are entities comprising user-defined identity, behavior, and state. Grain identities are user-defined keys which make make Grains always available for invocation. Grains can be invoked by other grains or by external clients such as Web frontends, via strongly-typed communication interfaces (contracts). Each grain is an instance of a class which implements one or more of these interfaces.

Grains can have volatile and/or persistent state that can be stored in any storage system. As such, grains implicitly partition application state, enabling automatic scalability and simplifying recovery from failures. Grain state is kept in memory while the grain is active, leading to lower latency and less load on data stores.

< insert managed lifecycle diagram? >

Instantiation of grains is automatically performed on demand by the Orleans runtime. Grains which are not used are automatically removed from memory to free up resources. This is possible because of their stable identity, which allows invoking grains whether they are already loaded into memory or not. This also allows for transparent recovery from failure because the caller does not need to know on which server a grain is instantiated on at any point in time. Grains have a managed lifecycle, with the Orleans runtime responsible for activating/deactivating, and placing/locating grains as needed. This allows the developer to write code as if all grains were always in-memory.

Taken together, the stable identity, statefulness, and managed lifecycle of Grains are core factors that make systems built on Orleans scalable, performant, &amp; reliable without forcing developers to write complex distributed systems code.

## Example: Internet of Things Backend

Consider a cloud backend for an [Internet of Things](https://en.wikipedia.org/wiki/Internet_of_things) system. This application needs to process incoming device data, filter, aggregate, and process this information and enable sending commands to devices. In Orleans it is natural to model each device with a grain which becomes a *digital twin* of the physical device it corresponds to. These grains keep the latest device data in memory so that it can be quickly queried and processed without the need to communicate with the device directly. By observing streams of time-series data from the device, the grain can detect changes in conditions, such as measurements exceeding a threshold, and trigger an action.

A simple thermostat could be modeled as follows:

``` C#
public interface IThermostat : IGrainWithStringKey
{
  Task<List<Command>> OnUpdate(ThermostatStatus update);
}
```

Events arriving from the thermostat from a Web frontend can be sent to its grain by invoking the `OnUpdate` method which optionally returns a command back to the device.

``` C#
var thermostat = client.GetGrain<IThermostat>(id);
return await thermostat.OnUpdate(update);
```

The same thermostat can implement a querying interface for control systems to interact with it:

``` C#
public interface IThermostatControl : IGrainWithStringKey
{
  Task<ThermostatStatus> GetLatest();

  Task UpdateConfiguration(ThermostatConfiguration config);
}
```

These two interfaces (`IThermostat` and `IThermostatControl`) are implemented by a single implementation class:

``` C#
public class ThermostatGrain : Grain, IThermostat, IThermostatControl
{
  private ThermostatStatus _status;
  private List<Command> _commands;

  public Task<List<Command>> OnUpdate(ThermostatStatus status)
  {
    _status = status;
    var result = _commands;
    _commands = new List<Command>();
    return Task.FromResult(result);
  }

  public Task<ThermostatStatus> GetLatest() => Task.FromResult(_status);
  
  public Task UpdateConfiguration(ThermostatConfiguration config)
  {
    _commands.Add(new ConfigUpdateCommand(config));
    return Task.CompletedTask;
  }
}
```

The grain class above does not persist its state. In order to persist state using a configured storage provider, some changes must be made. First, we will define a class to hold the state.

``` C#
[Serializable]
public class ThermostatState
{
  public ThermostatStatus Status { get; set; }
  public List<Command> Commands { get; set; } = new List<Command>();
}
```

Next, inject the persistent state into the grain's constructor:

```C#
public class ThermostatGrain : Grain, IThermostat, IThermostatControl
{
  IPersistentState<ThermostatState> _state;

  public ThermostatGrain(IPersistentState<ThermostatState> state) => _state = state;
}
```

Now we can rewrite the grain methods to access and update the persistent state:

```C#
public async Task<List<Command>> OnUpdate(ThermostatStatus status)
{
  var state = _state.Value;
  state.Status = status;
  var result = state.Commands;
  state.Commands = new List<Command>();

  // Persist the updated state.
  await _state.WriteStateAsync();

  return result;
}

public Task<ThermostatStatus> GetLatest() => Task.FromResult(_state.Value.Status);

public async Task UpdateConfiguration(ThermostatConfiguration config)
{
  _state.Value.Commands.Add(new ConfigUpdateCommand(config));
  await _state.WriteStateAsync();
}
```

#

## Features

### Persistence

### Transactions

## Packages

Installation is performed via [NuGet](https://www.nuget.org/packages?q=orleans). 
There are several packages, one for each different project type (interfaces, grains, silo, and client).

In the grain interfaces project:
```
PM> Install-Package Microsoft.Orleans.Core.Abstractions
PM> Install-Package Microsoft.Orleans.CodeGenerator.MSBuild
```
In the grain implementations project:
```
PM> Install-Package Microsoft.Orleans.Core.Abstractions
PM> Install-Package Microsoft.Orleans.CodeGenerator.MSBuild
```
In the server (silo) project:
```
PM> Install-Package Microsoft.Orleans.Server
```
In the client project:
```
PM> Install-Package Microsoft.Orleans.Client
```

## Official Builds

The stable production-quality release is located [here](https://github.com/dotnet/orleans/releases/latest).

The latest clean development branch build from CI is located: [here](https://ci.dot.net/job/dotnet_orleans/job/master/job/bvt/lastStableBuild/artifact/)

Nightly builds are published to https://dotnet.myget.org/gallery/orleans-ci . These builds pass all functional tests, but are not thoroughly tested as the stable builds or pre-release builds we push to NuGet.org

To use nightly builds in your project, add the MyGet feed using either of the following methods:

1. Changing the .csproj file to include this section:

```xml
  <RestoreSources>
    $(RestoreSources);
    https://dotnet.myget.org/F/orleans-ci/api/v3/index.json;
  </RestoreSources>
```

or

2. Creating a `NuGet.config` file in the solution directory with the following contents:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources>
  <clear />
  <add key="orleans-ci" value="https://dotnet.myget.org/F/orleans-ci/api/v3/index.json" />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
 </packageSources>
</configuration>
```

## Building

Clone the sources from the GitHub [repo](https://github.com/dotnet/orleans)

Run the `Build.cmd` script to build the NuGet packages locally,
then reference the required NuGet packages from `/Artifacts/Release/*`.
You can run `Test.cmd` to run all BVT tests, and `TestAll.cmd` to also run Functional tests (which take much longer)

## Documentation

Documentation is located [here](https://dotnet.github.io/orleans/Documentation/)

## Blog

[Orleans Blog](https://dotnet.github.io/orleans/blog/) is a place to share our thoughts, plans, learnings, tips and tricks, and ideas, crazy and otherwise, which don’t easily fit the documentation format. We would also like to see here posts from the community members, sharing their experiences, ideas, and wisdom. 
So, welcome to Orleans Blog, both as a reader and as a blogger!

## Community

* Ask questions by [opening an issue on GitHub](https://github.com/dotnet/orleans/issues) or on [Stack Overflow](https://stackoverflow.com/questions/ask?tags=orleans)

* [Chat on Gitter](https://gitter.im/dotnet/orleans)

* Follow the [@MSFTOrleans](https://twitter.com/MSFTOrleans) Twitter account for Orleans announcements.

* [OrleansContrib - Repository of community add-ons to Orleans](https://github.com/OrleansContrib/) Various community projects, including Orleans Monitoring, Design Patterns, Storage Provider, etc.

* Guidelines for developers wanting to [contribute code changes to Orleans](http://dotnet.github.io/orleans/Community/Contributing.html).

* You are also encouraged to report bugs or start a technical discussion by starting a new [thread](https://github.com/dotnet/orleans/issues) on GitHub.

## License

This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/master/LICENSE).

## Quick Links

* [Microsoft Research project page](http://research.microsoft.com/projects/orleans/)
* Technical Report: [Distributed Virtual Actors for Programmability and Scalability](http://research.microsoft.com/apps/pubs/default.aspx?id=210931)
* [Orleans Documentation](http://dotnet.github.io/orleans/)
* [Contributing](http://dotnet.github.io/orleans/Community/Contributing.html)

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
