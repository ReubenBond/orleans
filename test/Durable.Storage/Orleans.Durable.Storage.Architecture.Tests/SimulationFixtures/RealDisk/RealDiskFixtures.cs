namespace Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures.RealDisk;

/// <summary>A file system with a real-disk default, as the WAL's is.</summary>
internal sealed class FixtureFileSystem
{
    public static FixtureFileSystem Os { get; } = new();
}

/// <summary>A store whose default open and constructor reach the real disk, and whose others take a file system.</summary>
internal sealed class FixtureStore(string path, FixtureFileSystem fileSystem)
{
    public FixtureStore(string path)
        : this(path, FixtureFileSystem.Os)
    {
    }

    public string Path { get; } = path;

    public FixtureFileSystem FileSystem { get; } = fileSystem;

    public static FixtureStore OpenAsync(string path) => new(path);

    public static FixtureStore OpenAsync(string path, FixtureFileSystem fileSystem) => new(path, fileSystem);
}

/// <summary>A storage whose native library reaches the real disk.</summary>
internal sealed class FixtureNativeStorage;

/// <summary>
/// A simulation which reaches the real disk through each entry point, and through each simulated form besides: a
/// portable file over a simulated file system, beside the portable storage which reaches it over the real one.
/// </summary>
internal static class FixtureRealDiskUser
{
    public static object[] Reach(FixtureFileSystem simulated) =>
    [
        FixtureFileSystem.Os,
        FixtureStore.OpenAsync("root"),
        new FixtureStore("root"),
        new Storage.FixtureDiskStorage(),
        new FixtureNativeStorage(),
        FixtureStore.OpenAsync("root", simulated),
        new FixtureStore("root", simulated),
        Portable.FixturePortableStorage.Open("root"),
        Portable.FixturePortableFile.Open(simulated, "root"),
    ];
}
