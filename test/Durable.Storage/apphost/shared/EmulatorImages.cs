using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Orleans.Durable.Storage.AppHost;

/// <summary>An emulator's container image, pinned by its digest so a run starts the same bytes whatever its tags later name.</summary>
/// <param name="Image">The image's repository.</param>
/// <param name="Tag">The release the digest names, or <see langword="null"/> for an image published untagged.</param>
/// <param name="Digest">The image's SHA-256 digest, in hexadecimal.</param>
internal sealed record EmulatorImage(string Image, string? Tag, string Digest);

/// <summary>
/// The emulators' images, which apphost/Orleans.Durable.Storage.AppHost and samples/Orleans.Durable.Storage.Samples.AppHost both run, pinned once here
/// (docs/spec/build-packaging.md §2), and the container of each.
/// </summary>
internal static class EmulatorImages
{
    /// <summary>Gets Azurite, which accepts the newer service versions the Azure SDK names, and serves them as the versions it models.</summary>
    public static EmulatorImage Azurite { get; } = new("mcr.microsoft.com/azure-storage/azurite", "3.37.0", "830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5");

    /// <summary>Gets LocalStack: 4.14.0 is the last release whose community image serves S3 without an auth token.</summary>
    public static EmulatorImage LocalStack { get; } = new("localstack/localstack", "4.14.0", "3ebc37595918b8accb852f8048fef2aff047d465167edd655528065b07bc364a");

    /// <summary>
    /// Gets the Cloud Storage emulator, which checks preconditions and an upload's crc32c as the service does, and names a
    /// resumable session by the host its client addressed.
    /// </summary>
    public static EmulatorImage FakeGcsServer { get; } = new("fsouza/fake-gcs-server", "1.56.1", "797ce226d62f947c009dc40246b30cfb456b8473d8241407f9d6f2c04e4d69ef");

    /// <summary>
    /// Gets Google's Cloud Storage testbench, which answers a resumable session's status query as the service does, so the
    /// client's recovery of an interrupted upload runs against it.
    /// </summary>
    public static EmulatorImage GcsTestbench { get; } = new("gcr.io/cloud-devrel-public-resources/storage-testbench", "v0.67.0", "06729d407e8bba055798707dccfffd1f792ea818937954c56a776811116ede5c");

    /// <summary>Gets DynamoDB local, which is published untagged.</summary>
    public static EmulatorImage DynamoDb { get; } = new("amazon/dynamodb-local", null, "6ada631ec7aa5fc83446855eb17233e3dad184fec47477e8140c6c569cfe0c86");

    /// <summary>Gets the Google Cloud CLI's emulators, of which the AppHost runs Firestore's.</summary>
    public static EmulatorImage Firestore { get; } = new("gcr.io/google.com/cloudsdktool/google-cloud-cli", "emulators", "3989a42372d3b984bade6db02213d456d94a0ba3765e8b744b95c4188b6fcc89");

    /// <summary>Adds a container named <paramref name="name"/> of <paramref name="image"/>, pinned by its digest.</summary>
    public static IResourceBuilder<ContainerResource> AddEmulator(this IDistributedApplicationBuilder builder, string name, EmulatorImage image) =>
        (image.Tag is { } tag ? builder.AddContainer(name, image.Image, tag) : builder.AddContainer(name, image.Image)).WithImageSHA256(image.Digest);
}
