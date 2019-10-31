using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Diagnostics
{
    internal class RequestDiagnostics
    {
        public const string Category = "Microsoft.Orleans";

        public const string RequestInActivityName = Category + ".Request";
        public const string RequestInStartActivityName = RequestInActivityName + ".Start";
        public const string RequestInStopActivityName = RequestInActivityName + ".Stop";

        private readonly DiagnosticListener _diagnosticListener;
        private readonly ILogger _logger;

        public RequestDiagnostics(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger(Category);
            _diagnosticListener = new DiagnosticListener(Category);
        }

        public void RequestStart()
        {

        }
    }
}
