using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace Orleans.TestingHost.UnixSocketTransport;

public partial class UnixSocketConnectionOptions
{
    /// <summary>
    /// Get or sets to function used to get a filename given an endpoint
    /// </summary>
    public Func<EndPoint, string> ConvertEndpointToPath { get; set; } = DefaultConvertEndpointToPath;

    private static string DefaultConvertEndpointToPath(EndPoint endPoint) => Path.Combine(Path.GetTempPath(), SanitizeEndpointToStringRegex().Replace(endPoint.ToString(), "_"));

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex SanitizeEndpointToStringRegex();
}