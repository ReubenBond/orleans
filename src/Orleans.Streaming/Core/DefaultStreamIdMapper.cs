using System;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class DefaultStreamIdMapper : IStreamIdMapper
    {
        public const string Name = "default";

        public IdSpan GetGrainKeyId(GrainBindings grainBindings, StreamId streamId)
        {
            // Grain key is the stream key
            var key = streamId.Key;
            return MemoryMarshal.TryGetArray(key, out var seg) && seg.Offset == 0 && seg.Count == seg.Array.Length
                ? new IdSpan(seg.Array)
                : new IdSpan(key.ToArray());
        }
    }
}
