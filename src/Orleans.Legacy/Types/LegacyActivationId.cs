using System;
using Orleans.Runtime;

namespace Orleans.Legacy.Runtime
{
    [Serializable]
    internal class LegacyActivationId : UniqueIdentifier, IEquatable<LegacyActivationId>
    {
        public bool IsSystem { get { return Key.IsSystemTargetKey; } }

        public static readonly LegacyActivationId Zero;

        private static readonly Interner<UniqueKey, LegacyActivationId> interner;

        static LegacyActivationId()
        {
            interner = new Interner<UniqueKey, LegacyActivationId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq);
            Zero = FindOrCreate(UniqueKey.Empty);
        }

        /// <summary>
        /// Only used in Json serialization
        /// DO NOT USE TO CREATE A RANDOM ACTIVATION ID
        /// Use ActivationId.NewId to create new activation IDs.
        /// </summary>
        public LegacyActivationId()
        {
        }

        private LegacyActivationId(UniqueKey key)
            : base(key)
        {
        }

        public static LegacyActivationId NewId()
        {
            return FindOrCreate(UniqueKey.NewKey());
        }

        // No need to encode SiloAddress in the activation address for system target. 
        // System targets have unique grain ids and addressed to a concrete silo, so in fact we don't need ActivationId at all for System targets.
        // Need to remove it all together. For now, just use grain id as activation id.
        public static LegacyActivationId GetSystemActivation(LegacyGrainId grain, SiloAddress location)
        {
            if (!grain.IsSystemTarget)
                throw new ArgumentException("System activation IDs can only be created for system grains");
            return FindOrCreate(grain.Key);
        }

        internal static LegacyActivationId GetActivationId(UniqueKey key)
        {
            return FindOrCreate(key);
        }

        private static LegacyActivationId FindOrCreate(UniqueKey key)
        {
            return interner.FindOrCreate(key, k => new LegacyActivationId(k));
        }

        public override bool Equals(UniqueIdentifier obj)
        {
            var o = obj as LegacyActivationId;
            return o != null && Key.Equals(o.Key);
        }

        public override bool Equals(object obj)
        {
            var o = obj as LegacyActivationId;
            return o != null && Key.Equals(o.Key);
        }

        public bool Equals(LegacyActivationId other)
        {
            return other != null && Key.Equals(other.Key);
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public override string ToString()
        {
            string idString = Key.ToString().Substring(24, 8);
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }

        public string ToFullString()
        {
            string idString = Key.ToString();
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }
    }
}
