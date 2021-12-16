using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Legact.CodeGeneration;
using Orleans.Placement;
using Orleans.Runtime;

namespace Orleans.Legacy.Runtime
{
    /// <summary>
    /// Grain type meta data
    /// </summary>
    [Serializable]
    internal class GrainTypeData
    {
        internal Type Type { get; private set; }
        internal string GrainClass { get; private set; }
        internal List<Type> RemoteInterfaceTypes { get; private set; }
        internal bool IsReentrant { get; private set; }
        internal bool IsStatelessWorker { get; private set; }
   
        public GrainTypeData(Type type)
        {
            Type = type;
            this.IsReentrant = type.IsDefined(typeof(ReentrantAttribute), true);
            // TODO: shouldn't this use GrainInterfaceUtils.IsStatelessWorker?
            this.IsStatelessWorker = type.IsDefined(typeof(StatelessWorkerAttribute), true);
            this.GrainClass = TypeUtils.GetFullName(type);
            RemoteInterfaceTypes = GetRemoteInterfaces(type);
        }

        /// <summary>
        /// Returns a list of remote interfaces implemented by a grain class or a system target
        /// </summary>
        /// <param name="grainType">Grain or system target class</param>
        /// <returns>List of remote interfaces implemented by grainType</returns>
        private static List<Type> GetRemoteInterfaces(Type grainType)
        {
            var interfaceTypes = new List<Type>();

            while (grainType != typeof(Grain) && grainType != typeof(Object))
            {
                foreach (var t in grainType.GetInterfaces())
                {
                    if (t == typeof(IAddressable)) continue;

                    if (GrainInterfaceUtils.IsGrainInterface(t) && !interfaceTypes.Contains(t))
                        interfaceTypes.Add(t);
                }

                // Traverse the class hierarchy
                grainType = grainType.BaseType;
            }

            return interfaceTypes;
        }

        private static bool GetPlacementStrategy<T>(
            Type grainInterface, Func<T, PlacementStrategy> extract, out PlacementStrategy placement)
                where T : Attribute
        {
            var attribs = grainInterface.GetCustomAttributes<T>(inherit: true).ToArray();
            switch (attribs.Length)
            {
                case 0:
                    placement = null;
                    return false;

                case 1:
                    placement = extract(attribs[0]);
                    return placement != null;

                default:
                    throw new InvalidOperationException(
                        string.Format(
                            "More than one {0} cannot be specified for grain interface {1}",
                            typeof(T).Name,
                            grainInterface.Name));
            }
        }

#pragma warning disable 612,618
        internal static PlacementStrategy GetPlacementStrategy(Type grainClass, PlacementStrategy defaultPlacement)
        {
            PlacementStrategy placement;

            if (GetPlacementStrategy<PlacementAttribute>(
                grainClass,
                a => a.PlacementStrategy,
                out placement))
            {
                return placement;
            }

            return defaultPlacement;
        }

        internal static string GetGrainDirectory(Type grainClass)
        {
            var attr = grainClass.GetCustomAttribute<GrainDirectoryAttribute>();
            return attr != default ? attr.GrainDirectoryName : GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY;
        }
    }
}
