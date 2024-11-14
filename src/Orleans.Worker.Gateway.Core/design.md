 ## Design:
 - Allow multiple gateways to be registered with the system
 - Workers can update the local silo's manifest, augmenting it with their own, at which point the silo informs the cluster that its manifest has been updated (deduplicated)
 - LATER: Create a single IGrainContext instance per worker, which routes all requests for worker grains to that worker
   - Catalog contains GrainId -> WorkerGrainContext mapping for all worker grains, but the WorkerGrainContext is not unique per worker
 - Create per-remote-grain IGrainContext which proxies calls to a connected, compatible worker.
   - If no compatible worker is connected, it rejects messages or forwards them to a compatible worker
   - Tracks pending requests and rejects requests when the worker disconnects
 - [UNDECIDED] WorkerGrainContext is not collectible
   - Worker runtime is responsible for collecting grains and informing gateway of their removal
 - Custom IGrainActivator/IGrainActivatorProvider for worker grains, reading the worker's manifest and creating the appropriate context
 - WorkerRuntime handles worker grain activation, fetching state for the worker, etc.

 - IGrainContext calls into grain directory to register the grains registered to the worker
