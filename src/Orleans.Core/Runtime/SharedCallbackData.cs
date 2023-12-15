using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class SharedCallbackData
    {
        public readonly ILogger Logger;
        private TimeSpan responseTimeout;
        public long ResponseTimeoutStopwatchTicks;

        public SharedCallbackData(
            ILogger logger,
            TimeSpan responseTimeout)
        {
            this.Logger = logger;
            this.ResponseTimeout = responseTimeout;
        }

        public TimeSpan ResponseTimeout
        {
            get => this.responseTimeout;
            set
            {
                this.responseTimeout = value;
                this.ResponseTimeoutStopwatchTicks = (long)value.TotalMilliseconds;
            }
        }
    }
}