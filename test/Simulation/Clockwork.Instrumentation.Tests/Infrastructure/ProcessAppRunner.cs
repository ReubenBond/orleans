using System.Diagnostics;
using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// The captured result of running a staged fixture executable in a separate process.
/// </summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">The full standard-output text.</param>
/// <param name="StandardError">The full standard-error text.</param>
internal readonly record struct AppRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Gets the trimmed standard output for convenient assertions.</summary>
    public string Output => StandardOutput.Trim();
}

/// <summary>
/// Runs a compiled fixture assembly as a real out-of-process .NET application via
/// <c>dotnet exec</c>. build/tool integration proves an <em>enabled staged executable</em> dispatches to a
/// test shim while a <em>normal executable does not</em> - a claim only a separate process can make
/// honestly, because an in-process collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// cannot demonstrate a fully independent host resolving the rewritten closure from disk.
/// </summary>
internal static class ProcessAppRunner
{
    /// <summary>
    /// Writes a minimal framework-dependent <c>runtimeconfig.json</c> next to <paramref name="appAssemblyPath"/>
    /// so the shared .NET host can launch it. The running runtime's major/minor is targeted with
    /// <c>latestMinor</c> roll-forward, so any installed patch of the same line satisfies it.
    /// </summary>
    public static void WriteRuntimeConfig(string appAssemblyPath)
    {
        Version version = Environment.Version;
        string configPath = Path.ChangeExtension(appAssemblyPath, "runtimeconfig.json");
        string json = $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{version.Major}}.{{version.Minor}}",
                "rollForward": "latestMinor",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{version.Major}}.{{version.Minor}}.0"
                }
              }
            }
            """;
        File.WriteAllText(configPath, json);
    }

    /// <summary>Runs <paramref name="appAssemblyPath"/> with <c>dotnet exec</c> and captures its output.</summary>
    /// <param name="appAssemblyPath">The path to the managed entry assembly to run.</param>
    /// <param name="timeout">The maximum time to wait before the run is considered hung.</param>
    /// <returns>The captured exit code and streams.</returns>
    public static AppRunResult Run(string appAssemblyPath, TimeSpan? timeout = null)
    {
        var environment = new Dictionary<string, string>
        {
            // Force invariant, deterministic output regardless of the host machine's locale.
            ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        };

        return Execute(
            "dotnet",
            ["exec", appAssemblyPath],
            Path.GetDirectoryName(Path.GetFullPath(appAssemblyPath))!,
            environment,
            timeout ?? TimeSpan.FromSeconds(60));
    }

    /// <summary>Runs an arbitrary executable, capturing its exit code and output streams.</summary>
    /// <param name="fileName">The executable to launch (resolved against <c>PATH</c>).</param>
    /// <param name="arguments">The ordered argument list (each element is passed verbatim).</param>
    /// <param name="workingDirectory">The working directory for the process.</param>
    /// <param name="environment">Additional environment variables to set, or <see langword="null"/>.</param>
    /// <param name="timeout">The maximum time to wait before the run is considered hung.</param>
    /// <returns>The captured exit code and streams.</returns>
    public static AppRunResult Execute(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the timeout check and the kill.
            }

            throw new TimeoutException($"Process '{fileName}' did not exit within the timeout.");
        }

        // Ensure the async stream readers have flushed all buffered output.
        process.WaitForExit();

        return new AppRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
