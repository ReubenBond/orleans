using System;
using Orleans.Runtime;

namespace Orleans.Legacy.Runtime
{
    [Serializable]
    internal class LegacyActivationAddress
    {
        public LegacyGrainId Grain { get; private set; }
        public LegacyActivationId Activation { get; private set; }
        public SiloAddress Silo { get; private set; }

        public bool IsComplete
        {
            get { return Grain != null && Activation != null && Silo != null; }
        }

        private LegacyActivationAddress(SiloAddress silo, LegacyGrainId grain, LegacyActivationId activation)
        {
            Silo = silo;
            Grain = grain;
            Activation = activation;
        }

        public static LegacyActivationAddress NewActivationAddress(SiloAddress silo, LegacyGrainId grain)
        {
            var activation = LegacyActivationId.NewId();
            return GetAddress(silo, grain, activation);
        }

        public static LegacyActivationAddress GetAddress(SiloAddress silo, LegacyGrainId grain, LegacyActivationId activation)
        {
            // Silo part is not mandatory
            if (grain is null) throw new ArgumentNullException("grain");

            return new LegacyActivationAddress(silo, grain, activation);
        }

        public override bool Equals(object obj)
        {
            var other = obj as LegacyActivationAddress;
            return other != null && Equals(Silo, other.Silo) && Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }

        public override int GetHashCode()
        {
            return (Silo != null ? Silo.GetHashCode() : 0) ^
                (Grain != null ? Grain.GetHashCode() : 0) ^
                (Activation != null ? Activation.GetHashCode() : 0);
        }

        public override string ToString()
        {
            return String.Format("{0}{1}{2}", Silo, Grain, Activation);
        }

        public string ToFullString()
        {
            return
                String.Format(
                    "[ActivationAddress: {0}, Full GrainId: {1}, Full ActivationId: {2}]",
                    this.ToString(),                        // 0
                    this.Grain.ToFullString(),              // 1
                    this.Activation.ToFullString());        // 2
        }

        public bool Matches(LegacyActivationAddress other)
        {
            return Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }
    }
}
