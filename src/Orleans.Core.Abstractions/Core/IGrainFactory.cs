using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    // * The silo _receiving_ a message is responsible for compatibility checks.
    //   * If a message is not compatible with the grain on the receiving silo, it is responsible for finding a silo to replace it.
    //   * In that case, it can locally deactivate the grain and transfer responsibility to a new silo.
    //   * Otherwise, it must send a rejection message back to the caller

    public interface IMessageDispatcher_
    {
        // Calls IGrainCatalog.TryGetActivation
        // If found, call activation.OnMessage(message);
        // If not found, call IGrainPlacementDirector.LookupGrain(message.Destination)
        //   If found, forward message to resulting silo
        //   If not found, PLACE GRAIN ON COMPATIBLE SILO THEN FORWARD TO THAT SILO
        void DispatchMessage(IMessage_ message);
    }

    public interface IGrainCatalog_
    {
        // Try to find activation in local catalog.
        bool TryGetGrain(GrainId grainId, out IGrainHolder_ grain);

        bool GetOrAddGrain(GrainId grainId, out IGrainHolder_ grain);

        bool RemoveGrain(GrainId grainId, out IGrainHolder_ grain);

        // TODO: Needs some way to get all grains? (eg for re-registration in directories, deactivation during shutdown?)
    }

    // Service discovery for grains
    // This is what's responsible for calling into a grain directory (if needed)
    // This is responsible for routing for versionining & heterogeneous deployments
    // Analogous to PlacementDirectorsManager
    public interface IGrainLocator_
    {
        // What should this accept as args?
        //  It may need info about the interface being called.
        //      How is information like that propagated through the system?
        //          `VersionedGrainId` = `GrainId` + `int`? That doesn't tell us about the *type* being called (eg, imagine a grain extension).
        //          So it seems we need more information here. We could pass 
        // What should this return? Should it be a ~"silo address"/SiloId?

        // Let's say it returns a SiloAddress/SiloId and the receiver is responsible for versioning/hetero?
        // Finds the grain in the cluster.
        //   * First, get the grain's placement policy
        //   * Ask the placement policy to locate the grain
        //   * If the grain is not found, ask the placement policy to place the grain
        ValueTask<SiloId> LocateGrain(GrainId grainId);
    }

    // Minimal message: Destination, Metadata, Data
    public interface IMessage_
    {
        // To:
        GrainId TargetGrainId { get; }
        SiloId TargetSiloId { get; }

        // From:
        // Note - there's no "SendingGrainId" here. Messages very often do not come from a grain (eg, from a client, stream)
        long Id { get; set; }
        SiloId SendingSiloId { get; }

        // Catalog: IsNewPlacement - useful so the target silo doesn't re-run placement and forward to yet another silo...

        // TTL (TimeSpan, HopCount)

        // Request Context
        IDictionary<string, string> Metadata { get; }
        object Data { get; }
    }

    // Analogous to ActivationData
    public interface IGrainHolder_
    {
        public ValueTask OnCreate();
        void OnMessage(IMessage_ message); // TODO: remove messages from this level of abstraction? Invokables instead?
        public ValueTask OnDestroy();
    }

    public interface IGrainTypeResolver_
    {
        // Called by GrainFactory when creating a grain id.
        bool TryGetGrainType(Type interfaceType, out GrainType grainType);

        // Note: for legacy support (IGrainFactory.GetGrain overloads which take prefix)
        bool TryGetGrainType(Type interfaceType, string grainClassPrefix, out GrainType grainType);
    }

    public interface IGrainPlacementDirector_
    {
        // Locate the grain. If the grain is not known to be activated at the moment, then select a silo to host the activation.
        // TODO: Does this need more info? Eg, for versioning/compatibility?
        ValueTask<SiloId> LocateGrain(GrainId grainId);

        // Called on the target silo *before* a grain is activated.
        // The purpose is to perform any steps which must be performed before the grain begins execution.
        // For example, registering the grain in a directory. If registration fails, this would throw.
        ValueTask OnCreate(GrainId grainId);
    }

    public interface IGrainBuilder_
    {
        void OnRoute(GrainType grainType, ref GrainSpecification_ spec);
    }

    public class GrainSpecification_
    {
        public object PrimaryImplementation { get; set; }
    }

    public interface IGrainFactory
    {
        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey;

        /// <summary>
        /// Creates a reference to the provided <paramref name="obj"/>.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The object to create a reference to.</param>
        /// <returns>The reference to <paramref name="obj"/>.</returns>
        Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Binds the provided grain reference to this instance.
        /// </summary>
        /// <param name="grain">The grain reference.</param>
        void BindGrainReference(IAddressable grain);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, string grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension);
    }
}