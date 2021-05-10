using System;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Orleans.MetadataStore.Tests
{
    public class XunitLogger : ILogger
    {
        private readonly ITestOutputHelper output;
        private readonly string prefix;

        public XunitLogger(ITestOutputHelper output, string prefix)
        {
            this.output = output;
            this.prefix = prefix;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.output.WriteLine($"{prefix}: [{logLevel}] {formatter(state, exception)} {exception}");
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable BeginScope<TState>(TState state) => throw new NotImplementedException();
    }
}