using System;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public ISchedulingContext ActivationContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;
        public static RuntimeContext Current => context ??= new RuntimeContext();

        internal static ISchedulingContext CurrentActivationContext => Current?.ActivationContext;

        internal static void SetExecutionContext(ISchedulingContext shedContext)
        {
            Current.ActivationContext = shedContext;
        }

        internal static void ResetExecutionContext()
        {
            Current.ActivationContext = null;
        }

        public override string ToString()
        {
            return String.Format("RuntimeContext: ActivationContext={0}", 
                ActivationContext != null ? ActivationContext.ToString() : "null");
        }
    }
}
