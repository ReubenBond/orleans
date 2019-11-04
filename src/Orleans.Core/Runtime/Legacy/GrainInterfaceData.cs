using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
    /// <summary>
    /// Metadata for a grain interface
    /// </summary>
    [Serializable]
    internal class GrainInterfaceData
    {
        [NonSerialized]
        private readonly Type iface;
        private readonly HashSet<LegacyGrainClassData> implementations;

        internal Type Interface { get { return iface; } }
        internal int InterfaceId { get; private set; }
        internal ushort InterfaceVersion { get; private set; }
        internal string GrainInterface { get; private set; }
        internal LegacyGrainClassData[] Implementations { get { return implementations.ToArray(); } }
        internal LegacyGrainClassData PrimaryImplementation { get; private set; }

        internal GrainInterfaceData(int interfaceId, ushort interfaceVersion, Type iface, string grainInterface)
        {
            InterfaceId = interfaceId;
            InterfaceVersion = interfaceVersion;
            this.iface = iface;
            GrainInterface = grainInterface;
            implementations = new HashSet<LegacyGrainClassData>();
        }

        internal void AddImplementation(LegacyGrainClassData implementation, bool primaryImplemenation = false)
        {
            lock (this)
            {
                if (!implementations.Contains(implementation))
                    implementations.Add(implementation);

                if (primaryImplemenation)
                    PrimaryImplementation = implementation;
            }
        }

        public override string ToString()
        {
            return String.Format("{0}:{1}", GrainInterface, InterfaceId);
        }
    }
}
