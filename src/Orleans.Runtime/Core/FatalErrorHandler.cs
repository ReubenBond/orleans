using System;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System.Threading;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Orleans.Runtime
{
    internal class FatalErrorHandler : IFatalErrorHandler
    {
        private readonly ILogger<FatalErrorHandler> log;
        private readonly ClusterMembershipOptions clusterMembershipOptions;

        public FatalErrorHandler(
            ILogger<FatalErrorHandler> log,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions)
        {
            this.log = log;
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
        }

        public bool IsUnexpected(Exception exception)
        {
            return !(exception is ThreadAbortException);
        }

        public void OnFatalException(object sender, string context, Exception exception)
        {
            this.log.LogError(
                (int)ErrorCode.Logger_ProcessCrashing,
                exception,
                "Fatal error from {Sender}. Context: {Context}",
                sender,
                context);

            var msg = @$"FATAL EXCEPTION from {sender?.ToString() ?? "null"}. Context: {context ?? "null"}. Exception: {(exception != null ? LogFormatter.PrintException(exception) : "null")}.\nCurrent stack: {Environment.StackTrace}";
            Console.Error.WriteLine(msg);

            // Allow some time for loggers to flush.
            DumpCapture.CreateMiniDump(Process.GetCurrentProcess());
            Thread.Sleep(2000);

            if (Debugger.IsAttached) Debugger.Break();

            Environment.FailFast(msg, exception);
        }
    }

    internal static class DumpCapture
    {
        internal static FileInfo CreateMiniDump(Process process, MiniDumpType dumpType = MiniDumpType.MiniDumpWithFullMemory)
        {
            var dumpFileName = $@"{process.ProcessName}-MiniDump-{DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fffZ", CultureInfo.InvariantCulture)}.dmp";

            using (var stream = File.Create(dumpFileName))
            {
                var result = NativeMethods.MiniDumpWriteDump(
                    process.Handle,
                    process.Id,
                    stream.SafeFileHandle.DangerousGetHandle(),
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            return new FileInfo(dumpFileName);
        }

        private static class NativeMethods
        {
            [DllImport("Dbghelp.dll")]
            public static extern bool MiniDumpWriteDump(
                IntPtr hProcess,
                int processId,
                IntPtr hFile,
                MiniDumpType dumpType,
                IntPtr exceptionParam,
                IntPtr userStreamParam,
                IntPtr callbackParam);
        }

        internal enum MiniDumpType
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutManagedState = 0x00004000,
        }
    }
}
