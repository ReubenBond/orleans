# Distributed Chaos

This project is used to test fault tolerance in a distributed system. It consists of the following projects:

- DistributedChaos.Silo - an Orleans silo and forms a cluster with other silos.
- DistributedChaos.Worker - an Orleans client which issues work to the cluster.
- DistributedChaos.Frontend - a health and management dashboard which can be used to control the application.
  - Add or remove workers.
  - Add or remove silos.
  - Silo can be shutdown gracefully or ungracefully.

The frontend communicates with the Kubernetes API server to manage application scale.


