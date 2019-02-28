using System;
using System.Net;

namespace Orleans.Runtime.Messaging
{
    internal static class IPEndPointUtility
    {
        public static bool TryParseEndPoint(string value, out IPEndPoint result)
        {
            if (!Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri) ||
                !IPAddress.TryParse(uri.Host, out var ipAddress) ||
                uri.Port < IPEndPoint.MinPort || uri.Port > IPEndPoint.MaxPort)
            {
                result = default;
                return false;
            }

            result = new IPEndPoint(ipAddress, uri.Port);
            return true;
        }
    }
}
