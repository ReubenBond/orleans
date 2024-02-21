using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public IGrainContext GrainContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;

        public static RuntimeContext Current => context;

        internal static IGrainContext CurrentGrainContext { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => context?.GrainContext; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, out IGrainContext originalContext)
        {
            context ??= new RuntimeContext();
            originalContext = context.GrainContext;
            context.GrainContext = newContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext(IGrainContext originalContext)
        {
            context.GrainContext = originalContext;
        }

        public override string ToString() => $"RuntimeContext: GrainContext={GrainContext?.ToString() ?? "null"}";
    }
}
