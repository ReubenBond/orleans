---
title: ADO.NET database configuration
description: Find Orleans ADO.NET schema scripts and provider invariants.
ms.date: 08/02/2026
ms.topic: reference
---

# ADO.NET database configuration

Orleans keeps its ADO.NET schema scripts beside each provider's source. Run the main script before the capability scripts. Use scripts from the same Orleans release as the packages deployed by the application.

## Driver invariants

| Database | Driver package | Orleans invariant |
|---|---|---|
| SQL Server | [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient/) | `Microsoft.Data.SqlClient` |
| PostgreSQL | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| MySQL/MariaDB | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| Oracle | [Oracle.ManagedDataAccess.Core](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/) | `Oracle.DataAccess.Client` |

> [!IMPORTANT]
> Use `Microsoft.Data.SqlClient`, not `System.Data.SqlClient`, for SQL Server.

## Main scripts

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/SQLServer-Main.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/PostgreSQL-Main.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/MySQL-Main.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Oracle-Main.sql)
- [SQLite](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Sqlite-Main.sql) for supported local persistence scenarios

## Clustering

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql)

The clustering queries implement the [canonical membership view contract](../../implementation/cluster-management.md#membership-table): row changes and the next version commit atomically, reads pair rows with their committed version, and liveness writes retain the maximum timestamp.

Existing databases receive these query changes through the matching membership-consistency migration:

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/SQLServer-Clustering-MembershipConsistency.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/PostgreSQL-Clustering-MembershipConsistency.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/MySQL-Clustering-MembershipConsistency.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/Oracle-Clustering-MembershipConsistency.sql)

Stop every silo sharing these database membership queries, apply the migration after earlier schema migrations, deploy the updated provider to every silo, and then restart. Providers cache `OrleansQuery` text, the cleanup queries use updated parameters, and MySQL routine replacement uses nontransactional DDL, so this upgrade requires a quiesced cluster.

The migration updates stored queries and routines while preserving the table layout. Updating the application package alone leaves the database's existing `OrleansQuery` entries and routines in use. Grant runtime credentials permission to execute the membership cleanup routines added for MySQL, PostgreSQL, and Oracle, and the MySQL `UpdateMembershipKey` routine. Deployment credentials need permission to manage routines and update `OrleansQuery`. Validate the resulting schema with the membership conformance suite against the same database engine and driver used in production.

## Persistence

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)
- [SQLite](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Sqlite-Persistence.sql)

## Reminders

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/MySQL-Reminders.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/Oracle-Reminders.sql)

## Grain directory scripts

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/SQLServer-GrainDirectory.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/PostgreSQL-GrainDirectory.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/MySQL-GrainDirectory.sql)

Not every capability supports every database. The presence of a script in the provider directory is the authoritative support signal for that Orleans release.

## Apply and upgrade schemas

1. Back up application data according to the database recovery policy.
2. Apply the main script for a new database.
3. Apply the script for each configured Orleans capability.
4. Review and apply scripts under the provider's `Migrations` directory when upgrading from an older schema.
5. Validate with a staging cluster using the same driver and database engine version.

## Production database layout

The supplied scripts are a starting schema, not a universal production
database layout. They create unqualified table names in the database user's
default schema, and the ADO.NET providers do not expose schema or filegroup
configuration. A production deployment may choose a dedicated schema,
filegroups, partitioning, or other database-specific storage features, but
those choices must be implemented in the database deployment and kept
consistent with the queries stored in `OrleansQuery`. Preserve the table names,
columns, parameters, and result shapes expected by the provider, and validate
the customized scripts and queries with a staging cluster before rollout.

See [Configure ADO.NET providers](configuring-ado-dot-net-providers.md) for host configuration.
