using System.ComponentModel;
using System.Globalization;

namespace Orleans.Durable.Storage.Diagnostics;

/// <summary>
/// A report to <see cref="FatalErrorHandler"/>: a failed local storage operation, which a storage seam reported, or a
/// defect which would leave a component's memory out of step with its write-ahead log, which the component reported.
/// </summary>
/// <param name="Site">The reporting site, whose value is the process's exit code.</param>
/// <param name="Path">The file or directory a failed storage operation named; <see langword="null"/> for a defect.</param>
/// <param name="Exception">The operating system's or the storage engine's error, or the defect's exception.</param>
/// <param name="Defect">The defect's name, which its reporter gives it; <see langword="null"/> for a storage failure.</param>
public sealed record FatalError(FatalErrorSite Site, string? Path, Exception Exception, string? Defect = null)
{
    // HRESULTs which wrap a Win32 error: FACILITY_WIN32 with the severity bit (HRESULT_FROM_WIN32).
    private const int Win32FacilityMask = unchecked((int)0xFFFF0000);
    private const int Win32Facility = unchecked((int)0x80070000);

    // The largest errno any supported Unix defines is far below this; .NET puts the errno itself in HResult on Unix.
    private const int LargestErrno = 4095;

    /// <summary>
    /// Gets a value indicating whether the report is a defect rather than a failed local storage operation.
    /// </summary>
    public bool IsDefect => Site == FatalErrorSite.Defect;

    /// <summary>
    /// Gets the operating system's error code which the failure, or an exception it carries, holds, if any: a
    /// <see cref="Win32Exception"/>'s native error; on Windows, the Win32 error an <see cref="Exception.HResult"/> wraps;
    /// and on Unix the <c>errno</c>, which .NET puts in the <see cref="Exception.HResult"/> of an
    /// <see cref="IOException"/>. A generic or Windows HResult on Unix, such as <c>COR_E_IO</c>, carries none.
    /// </summary>
    public int? OperatingSystemError
    {
        get
        {
            for (var exception = Exception; exception is not null; exception = exception.InnerException)
            {
                if (exception is Win32Exception win32)
                {
                    return win32.NativeErrorCode;
                }

                var result = exception.HResult;
                if (OperatingSystem.IsWindows())
                {
                    if ((result & Win32FacilityMask) == Win32Facility)
                    {
                        return result & 0xFFFF;
                    }
                }
                else if (result is > 0 and <= LargestErrno)
                {
                    // .NET wraps the errno's IOException in an UnauthorizedAccessException or a DirectoryNotFoundException,
                    // whose own HResult is a Windows code; the search goes on to the inner exception.
                    return result;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns the message the process writes before it exits: the exit code, and the defect's name or, for a storage
    /// failure, the site, the path, and the operating system's error.
    /// </summary>
    public string Describe()
    {
        var error = string.Create(CultureInfo.InvariantCulture, $"{Exception.GetType().FullName}: {Exception.Message}");
        if (IsDefect)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Durable storage stopped the process with exit code {(int)Site}: the defect {Defect}, after {error}");
        }

        return string.Create(
                CultureInfo.InvariantCulture,
                $"Durable storage stopped the process with exit code {(int)Site}: the local storage operation {Site} on '{Path}' failed with {error}")
            + (OperatingSystemError is { } code ? string.Create(CultureInfo.InvariantCulture, $" (operating-system error {code})") : string.Empty);
    }
}
