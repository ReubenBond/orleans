namespace Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures.RealDisk.Portable;

/// <summary>A storage which gives its files the real-disk file system, as the WAL's portable storage does.</summary>
internal sealed class FixturePortableStorage
{
    public static FixturePortableFile Open(string path) => FixturePortableFile.Open(FixtureFileSystem.Os, path);
}

/// <summary>A file whose I/O goes through the file system it is given, as the portable storage's files do.</summary>
internal sealed class FixturePortableFile(FixtureFileSystem fileSystem, string path)
{
    public FixtureFileSystem FileSystem { get; } = fileSystem;

    public string Path { get; } = path;

    public static FixturePortableFile Open(FixtureFileSystem fileSystem, string path) => new(fileSystem, path);
}
