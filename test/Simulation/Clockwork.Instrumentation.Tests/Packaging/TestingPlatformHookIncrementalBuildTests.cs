using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

public sealed class TestingPlatformHookIncrementalBuildTests
{
    private const string OldHookId = "9F2B15B0-6B0E-4AAF-A66C-DC2CA4D5D01B";
    private const string NewHookId = "2E7C3A6B-6F8D-4F4B-9D52-1A0D7C8E5B93";
    private const string OldHookType = "Clockwork.Testing.TestingPlatformBuilderHook";
    private const string NewHookType = "Orleans.Testing.Simulation.Testing.TestingPlatformBuilderHook";

    [Fact]
    public void ChangedHookTypeRequiresNewIdentityToRefreshGeneratedRegistration()
    {
        string directory = TestArtifacts.CreateUnique("orleans-mtp-hook-incremental");
        WriteProject(directory);
        WriteHookType(directory, "Clockwork.Testing");
        WriteHookProps(directory, OldHookId, OldHookType);

        AppRunResult initial = Build(directory, noRestore: false);

        Assert.True(
            initial.ExitCode == 0,
            $"Initial build failed:\n{initial.StandardOutput}\n{initial.StandardError}");
        string oldRegistrationPath = RegistrationPath(directory, OldHookId);
        Assert.Contains(OldHookType, File.ReadAllText(oldRegistrationPath), StringComparison.Ordinal);

        WriteHookType(directory, "Orleans.Testing.Simulation.Testing");
        WriteHookProps(directory, OldHookId, NewHookType);

        AppRunResult stale = Build(directory, noRestore: true);

        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains(OldHookType, File.ReadAllText(oldRegistrationPath), StringComparison.Ordinal);
        Assert.DoesNotContain(NewHookType, File.ReadAllText(oldRegistrationPath), StringComparison.Ordinal);

        WriteHookProps(directory, NewHookId, NewHookType);

        AppRunResult refreshed = Build(directory, noRestore: true);

        Assert.True(
            refreshed.ExitCode == 0,
            $"Build after changing the hook identity failed:\n{refreshed.StandardOutput}\n{refreshed.StandardError}");
        string newRegistrationPath = RegistrationPath(directory, NewHookId);
        string newRegistration = File.ReadAllText(newRegistrationPath);
        Assert.Contains(NewHookType, newRegistration, StringComparison.Ordinal);
        Assert.DoesNotContain(OldHookType, newRegistration, StringComparison.Ordinal);
    }

    private static AppRunResult Build(string directory, bool noRestore)
    {
        var arguments = new List<string>
        {
            "build",
            "HookFixture.csproj",
            "--nologo",
            "--verbosity:minimal",
        };
        if (noRestore)
        {
            arguments.Add("--no-restore");
        }

        return ProcessAppRunner.Execute(
            "dotnet",
            arguments,
            directory,
            timeout: TimeSpan.FromMinutes(2));
    }

    private static string RegistrationPath(string directory, string hookId) =>
        Path.Combine(directory, "obj", $"HookRegistration.{hookId}.g.cs");

    private static void WriteHookProps(string directory, string hookId, string hookType) =>
        File.WriteAllText(
            Path.Combine(directory, "Hook.props"),
            $$"""
            <Project>
              <PropertyGroup>
                <MtpHookId>{{hookId}}</MtpHookId>
                <MtpHookType>{{hookType}}</MtpHookType>
              </PropertyGroup>
            </Project>
            """);

    private static void WriteHookType(string directory, string hookNamespace) =>
        File.WriteAllText(
            Path.Combine(directory, "TestingPlatformBuilderHook.cs"),
            $$"""
            namespace {{hookNamespace}};

            public static class TestingPlatformBuilderHook
            {
            }
            """);

    private static void WriteProject(string directory) =>
        File.WriteAllText(
            Path.Combine(directory, "HookFixture.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Hook.props" />
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <GeneratedHookRegistration>$(BaseIntermediateOutputPath)HookRegistration.$(MtpHookId).g.cs</GeneratedHookRegistration>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(GeneratedHookRegistration)" />
              </ItemGroup>
              <Target Name="GenerateHookRegistration"
                      BeforeTargets="CoreCompile"
                      Condition="!Exists('$(GeneratedHookRegistration)')">
                <MakeDir Directories="$([System.IO.Path]::GetDirectoryName('$(GeneratedHookRegistration)'))" />
                <ItemGroup>
                  <_HookRegistrationSource Include="namespace Generated%3B" />
                  <_HookRegistrationSource Include="internal static class HookRegistration" />
                  <_HookRegistrationSource Include="{" />
                  <_HookRegistrationSource Include="    internal static global::System.Type HookType => typeof(global::$(MtpHookType))%3B" />
                  <_HookRegistrationSource Include="}" />
                </ItemGroup>
                <WriteLinesToFile
                    File="$(GeneratedHookRegistration)"
                    Lines="@(_HookRegistrationSource)"
                    Overwrite="true" />
              </Target>
            </Project>
            """);
}
