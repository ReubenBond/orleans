---
layout: page
title: Configuring ADO.NET providers
uid: adonet_configuration
---

# Configuring ADO.NET providers

Any reliable deployment of Orleans requires using persistent storage to keep system state, specifically Orleans cluster membership table, persistence, and reminders.
One of the available options is using a SQL database via the ADO.NET providers.

In order to use ADO.NET for persistence, clustering, or reminders, the developer needs to configure the ADO.NET providers as part of the silo configuration, and, in case of clustering, also as part of the client configurations.

The silo configuration code should look like this:

``` csharp
var siloHostBuilder = new SiloHostBuilder();
var invariant = "System.Data.SqlClient"; // for Microsoft SQL Server
var connectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";

//use ADO.NET for clustering
siloHostBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });

//use ADO.NET for reminder service
siloHostBuilder.UseAdoNetReminderService(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });

//use ADO.NET for Persistence
siloHostBuilder.AddAdoNetGrainStorage("GrainStorageForTest", options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
```

The client configuration code should look like this:

``` csharp
var siloHostBuilder = new SiloHostBuilder();
var invariant = "System.Data.SqlClient";
var connectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";

// use ADO.NET for clustering
siloHostBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
```

Where the `ConnectionString` is set to a valid ADO.NET connection string.

In order to use ADO.NET providers for persistence, reminders or clustering, there are scripts for creating database artifacts, to which all servers that will be hosting Orleans silos need to have access.
Lack of access to the target database is a typical mistake we see developers making.

ADO.NET packages are separated by feature:
`Microsoft.Orleans.Clustering.AdoNet` for clustering, `Microsoft.Orleans.Persistence.AdoNet` for persistence and `Microsoft.Orleans.Reminders.AdoNet` for reminders.

## ADO.NET provider-specific configuration

The following sections contain links to SQL scripts to configure your database as well as the corresponding ADO.NET invariant used to configure ADO.NET providers in Orleans. These scripts are intended to be customized if needed for your deployment.

### Clustering

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |

### Persistence

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |

### Reminders

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/MySQL-Reminders.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/Oracle-Reminders.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |
