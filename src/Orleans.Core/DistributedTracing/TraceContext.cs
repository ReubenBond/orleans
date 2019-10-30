using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime
{
    internal class TraceContext
    {
        public Guid ActivityId { get; set; }
    }
}
