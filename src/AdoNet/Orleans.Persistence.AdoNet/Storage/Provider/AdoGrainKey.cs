using System;
using System.Globalization;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// This is an internal helper class that collects grain key information
    /// so that's easier to manage during database operations.
    /// </summary>
    internal readonly struct AdoGrainKey
    {
        public string Type { get; }

        public string Key { get; }

        public AdoGrainKey(GrainId id)
        {
            Type = id.Type.ToString();
            Key = id.Key.ToString();
        }

        public byte[] GetHashBytes()
        {
            var typeBytes = Encoding.UTF8.GetBytes(Type);
            var keyBytes = Encoding.UTF8.GetBytes(Key);

            var bytes = new byte[typeBytes.Length + keyBytes.Length];
            typeBytes.CopyTo(bytes, 0);
            keyBytes.CopyTo(bytes, bytes.Length);
            return bytes;
        }

        public override string ToString() => string.Format($"{Type}/{Key}");
    }
}
