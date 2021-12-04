using System;
using System.Diagnostics;

namespace Orleans.Legacy.Metadata
{
    /// <summary>
    /// Describes a grain interface.
    /// </summary>
    [DebuggerDisplay("{" + nameof(InterfaceType) + "}")]
    public class GrainInterfaceMetadata
    {
        /// <summary>
        /// Initializes an instance of the <see cref="GrainInterfaceMetadata"/> class.
        /// </summary>
        /// <param name="interfaceType">The grain interface type</param>
        /// <param name="referenceTypeName">The grain reference type.</param>
        /// <param name="interfaceId">The interface id.</param>
        public GrainInterfaceMetadata(Type interfaceType, string referenceTypeName, int interfaceId)
        {
            this.InterfaceType = interfaceType;
            this.ReferenceTypeName = referenceTypeName;
            this.InterfaceId = interfaceId;
        }

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        public Type InterfaceType { get; }

        /// <summary>
        /// Gets the type of the grain reference for this interface.
        /// </summary>
        public string ReferenceTypeName { get; }

        /// <summary>
        /// Gets the interface id.
        /// </summary>
        public int InterfaceId { get; }
    }
}