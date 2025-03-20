// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace NonSilo.Tests.Utilities
{
    internal class DelegateAsyncTimer : IAsyncTimer
    {
        private readonly Func<TimeSpan?, Task<bool>> nextTick;

        public DelegateAsyncTimer(Func<TimeSpan?, Task<bool>> nextTick)
        {
            this.nextTick = nextTick;
        }

        public int DisposedCounter { get; private set; }

        public Task<bool> NextTick(TimeSpan? overrideDelay = null) => this.nextTick(overrideDelay);

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = default;
            return true;
        }

        public void Dispose() => ++this.DisposedCounter;
    }
}
