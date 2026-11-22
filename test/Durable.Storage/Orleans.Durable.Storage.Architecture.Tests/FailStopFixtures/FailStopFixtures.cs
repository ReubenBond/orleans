using Orleans.Durable.Storage.Diagnostics;

namespace Orleans.Durable.Storage.Architecture.Tests.FailStopFixtures;

/// <summary>
/// A type outside the storage seams which reads a file, which <see cref="LocalStorageFailureTests"/>'s seam rule reports.
/// </summary>
internal static class ReadsAFile
{
    internal static string Read(string path) => File.ReadAllText(path);
}

/// <summary>
/// A type which ends the process, fails it fast, hooks its exit, and intercepts the fatal-error handler, each of which
/// <see cref="LocalStorageFailureTests"/>'s exit rule reports.
/// </summary>
internal static class StopsTheProcess
{
    internal static void Exit() => Environment.Exit(1);

    internal static void FailFast() => Environment.FailFast("A fixture.");

    internal static void Hook() => AppDomain.CurrentDomain.ProcessExit += static (_, _) => { };

    internal static void Intercept() => FatalErrorHandler.Interceptor = null;
}

/// <summary>
/// A type which reports a failed local storage operation and a defect, which <see cref="LocalStorageFailureTests"/>'s
/// reporter rule names among the reporters of each.
/// </summary>
internal static class ReportsFatalErrors
{
    internal static void Storage(Exception exception) => FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, "file", exception);

    internal static void Defect(Exception exception) => FatalErrorHandler.StopOnDefect("WalRoll", exception);
}
