using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// End-to-end golden tests for the shipped uncontrolled-invocation rule family: they
/// rewrite real <see cref="System.Diagnostics.Process"/> and <see cref="System.Environment"/> call sites in
/// a compiled fixture against the real <c>Clockwork</c> assembly and assert that each site is
/// rejected - a throwing <see cref="Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard.Reject(string)"/> is
/// injected before the original call, which the manifest records as a <see cref="SimulationApiPolicy.Rejected"/>
/// transformation naming the exact API. This proves a rewritten assembly cannot launch, kill, wait on, reschedule, or
/// terminate a real OS process.
/// </summary>
public sealed class UncontrolledInvocationRuleGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Runtime.InteropServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Win32.SafeHandles;

        namespace Fx
        {
            public static class ProcessUser
            {
                public static Process StartFile() => Process.Start("app.exe");
                public static Process StartInfo(ProcessStartInfo psi) => Process.Start(psi);
                public static Process StartArgs() => Process.Start("app.exe", "--flag");
                public static bool StartInstance(Process p) => p.Start();
                public static void Kill(Process p) => p.Kill();
                public static void KillTree(Process p) => p.Kill(true);
                public static bool WaitMs(Process p) => p.WaitForExit(100);
                public static void WaitAll(Process p) => p.WaitForExit();
                public static Task WaitAsync(Process p, CancellationToken ct) => p.WaitForExitAsync(ct);
                public static ProcessExitStatus Run() => Process.Run("app.exe");
                public static ProcessExitStatus RunInfo(ProcessStartInfo psi) => Process.Run(psi);
                public static Task<ProcessExitStatus> RunAsync() => Process.RunAsync("app.exe");
                public static Task<ProcessExitStatus> RunInfoAsync(ProcessStartInfo psi) => Process.RunAsync(psi);
                public static ProcessTextOutput Capture() => Process.RunAndCaptureText("app.exe");
                public static ProcessTextOutput CaptureInfo(ProcessStartInfo psi) => Process.RunAndCaptureText(psi);
                public static Task<ProcessTextOutput> CaptureAsync() => Process.RunAndCaptureTextAsync("app.exe");
                public static Task<ProcessTextOutput> CaptureInfoAsync(ProcessStartInfo psi) => Process.RunAndCaptureTextAsync(psi);
                public static int Forget() => Process.StartAndForget("app.exe");
                public static int ForgetInfo(ProcessStartInfo psi) => Process.StartAndForget(psi);
                public static bool Signal(Process p) => p.Signal(PosixSignal.SIGTERM);
                public static ProcessExitStatus WaitStatus(Process p) => p.WaitForExitStatus();
                public static bool TryWaitStatus(Process p) => p.TryWaitForExitStatus(TimeSpan.Zero, out _);
                public static Task<ProcessExitStatus> WaitStatusAsync(Process p) => p.WaitForExitStatusAsync();
                public static (byte[], byte[]) Bytes(Process p) => p.ReadAllBytes();
                public static Task<(byte[], byte[])> BytesAsync(Process p) => p.ReadAllBytesAsync();
                public static (string, string) Text(Process p) => p.ReadAllText();
                public static Task<(string, string)> TextAsync(Process p) => p.ReadAllTextAsync();
                public static IEnumerable<ProcessOutputLine> Lines(Process p) => p.ReadAllLines();
                public static IAsyncEnumerable<ProcessOutputLine> LinesAsync(Process p) => p.ReadAllLinesAsync();
                public static SafeProcessHandle HandleStart(ProcessStartInfo psi) => SafeProcessHandle.Start(psi);
                public static bool HandleSignal(SafeProcessHandle h) => h.Signal(PosixSignal.SIGTERM);
                public static void HandleResume(SafeProcessHandle h) => h.Resume();
                public static void HandleKill(SafeProcessHandle h) => h.Kill();
                public static ProcessExitStatus HandleWait(SafeProcessHandle h) => h.WaitForExit();
                public static bool HandleTryWait(SafeProcessHandle h) => h.TryWaitForExit(TimeSpan.Zero, out _);
                public static Task<ProcessExitStatus> HandleWaitAsync(SafeProcessHandle h) => h.WaitForExitAsync();
                public static ProcessExitStatus HandleWaitOrKill(SafeProcessHandle h) => h.WaitForExitOrKillOnTimeout(TimeSpan.Zero);
                public static Task<ProcessExitStatus> HandleWaitOrKillAsync(SafeProcessHandle h, CancellationToken ct) => h.WaitForExitOrKillOnCancellationAsync(ct);
                public static Process WindowsStart(ProcessStartInfo psi) => WindowsProcessStartArguments.Start(psi, _ => IntPtr.Zero);
                public static Process UnixStart(ProcessStartInfo psi) => UnixProcessStartArguments.Start(psi, _ => 0);
                public static void Exit() => Environment.Exit(1);
                public static void FailFast() => Environment.FailFast("boom");
                public static void FailFastEx(Exception ex) => Environment.FailFast("boom", ex);
                public static bool WaitIdle(Process p) => p.WaitForInputIdle();
                public static bool WaitIdleMs(Process p) => p.WaitForInputIdle(5);
                public static bool WaitIdleSpan(Process p) => p.WaitForInputIdle(TimeSpan.Zero);
                public static bool CloseWindow(Process p) => p.CloseMainWindow();
                public static bool Responding(Process p) => p.Responding;
                public static void RaiseEvents(Process p) => p.EnableRaisingEvents = true;
                public static void ReadOutput(Process p) => p.BeginOutputReadLine();
                public static void ReadError(Process p) => p.BeginErrorReadLine();
                public static void PriorityClass(Process p) => p.PriorityClass = ProcessPriorityClass.High;
                public static void PriorityBoost(Process p) => p.PriorityBoostEnabled = true;
                public static void Affinity(Process p) => p.ProcessorAffinity = (IntPtr)1;
                public static void MaxWorkingSet(Process p) => p.MaxWorkingSet = (IntPtr)1;
                public static void MinWorkingSet(Process p) => p.MinWorkingSet = (IntPtr)1;
                public static void IdealProcessor(ProcessThread t) => t.IdealProcessor = 0;
                public static void ResetIdealProcessor(ProcessThread t) => t.ResetIdealProcessor();
                public static void ThreadPriorityBoost(ProcessThread t) => t.PriorityBoostEnabled = true;
                public static void ThreadPriority(ProcessThread t) => t.PriorityLevel = ThreadPriorityLevel.Highest;
                public static void ThreadAffinity(ProcessThread t) => t.ProcessorAffinity = (IntPtr)1;
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard).Assembly.Location;

    private static RewriteResult RewriteFixture(RewriteTestContext context, string assemblyName)
    {
        string fixturePath = context.CompileFixture(assemblyName, Fixture);
        string outputPath = Path.Combine(
            context.Directory, Path.GetFileNameWithoutExtension(fixturePath) + ".rewritten.dll");

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        var result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
        result.EnsureSuccess();
        return result;
    }

    [Fact]
    public void ProcessAndEnvironmentCallSitesAreRejected()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.Uncontrolled");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.Uncontrolled.rewritten.dll"));

        foreach (var method in new[]
        {
            "StartFile", "StartInfo", "StartArgs", "StartInstance", "Kill", "KillTree",
            "WaitMs", "WaitAll", "WaitAsync", "Run", "RunInfo", "RunAsync", "RunInfoAsync",
            "Capture", "CaptureInfo", "CaptureAsync", "CaptureInfoAsync", "Forget", "ForgetInfo",
            "Signal", "WaitStatus", "TryWaitStatus", "WaitStatusAsync", "Bytes", "BytesAsync",
            "Text", "TextAsync", "Lines", "LinesAsync", "HandleStart", "HandleSignal", "HandleResume", "HandleKill",
            "HandleWait", "HandleTryWait", "HandleWaitAsync", "HandleWaitOrKill", "HandleWaitOrKillAsync",
            "WindowsStart", "UnixStart", "Exit", "FailFast", "FailFastEx", "WaitIdle", "WaitIdleMs", "WaitIdleSpan",
            "CloseWindow", "Responding", "RaiseEvents", "ReadOutput", "ReadError", "PriorityClass", "PriorityBoost", "Affinity",
            "MaxWorkingSet", "MinWorkingSet", "IdealProcessor", "ResetIdealProcessor", "ThreadPriorityBoost",
            "ThreadPriority", "ThreadAffinity",
        })
        {
            MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.ProcessUser", method);
            Assert.True(
                CecilInspect.CallsAnyContaining(definition, "UncontrolledInvocationGuard::Reject"),
                $"{method} should be rejected");
        }

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.process.start.filename" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.environment.exit" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.process.waitforexitasync" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.process.set_enableraisingevents" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.processthread.set_processoraffinity" && t.Policy == SimulationApiPolicy.Rejected);
    }
}
