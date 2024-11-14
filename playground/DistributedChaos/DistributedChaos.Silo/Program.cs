using System.Data.SqlClient;
using Orleans.Configuration;
using StackExchange.Redis;

while (true)
{
    try
    {
        Console.WriteLine("Initializing");
        using (var connection = new SqlConnection("Server=sqlserver,1433;User Id=SA;Password=yourWeak(!)Password;"))
        {
            await connection.OpenAsync();

            var dbName = "Orleans";
            var dbExists = false;
            {
                var exists = $"SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = '{dbName}'";
                using var cmd = connection.CreateCommand();
                cmd.CommandText = exists;
                var rawRes = await cmd.ExecuteScalarAsync();
                dbExists = rawRes is bool b ? b : false;
            }

            if (!dbExists)
            {
                Console.WriteLine("Create DB");
                var create = $@"USE [Master];
                    DECLARE @fileName AS NVARCHAR(255) = CONVERT(NVARCHAR(255), SERVERPROPERTY('instancedefaultdatapath')) + N'{0}';
                    EXEC('CREATE DATABASE [{dbName}] ON PRIMARY
                    (
                        NAME = [{dbName}],
                        FILENAME =''' + @fileName + ''',
                        SIZE = 20MB,
                        MAXSIZE = 10000MB,
                        FILEGROWTH = 5MB
                    )')";
                using var cmd = connection.CreateCommand();
                cmd.CommandText = create;
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("Created DB");
            }

            await connection.CloseAsync();
        }

        using (var connection = new SqlConnection("Server=sqlserver,1433;User Id=SA;Password=yourWeak(!)Password;Database=Orleans"))
        {
            await connection.OpenAsync();

            {
                Console.WriteLine("Executing one");
                var file = File.ReadAllText("SQLServer-Main.sql");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = file;
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("Done one");
            }
            {
                Console.WriteLine("Executing two");
                var file = File.ReadAllText("SQLServer-Clustering.sql");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = file;
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("Done two");
            }
            {
                Console.WriteLine("Executing three");
                var file = File.ReadAllText("SQLServer-Clustering-3.7.0.sql");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = file;
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("Done three");
            }

            await connection.CloseAsync();
        }

        break;
    }
    catch (Exception e)
    {
        Console.WriteLine(e);
        await Task.Delay(5000);
    }
}

Console.WriteLine("Starting program");

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Host.UseOrleans((ctx, orleans) =>
{
    orleans.AddDistributedGrainDirectory();

    orleans.AddActivationRebalancer();

    // aggressive settings our demo
    orleans.Configure<ActivationRebalancerOptions>(o =>
    {
        o.SessionCyclePeriod = TimeSpan.FromSeconds(2);
        o.RebalancerDueTime = TimeSpan.FromSeconds(5);
        o.CycleNumberWeight = 1;
        o.SiloNumberWeight = 0;
    });

    if (ctx.HostingEnvironment.IsDevelopment())
    {
        orleans.UseLocalhostClustering();
    }
    else
    {
        orleans.UseAdoNetClustering(o =>
        {
            o.ConnectionString = "Server=sqlserver,1433;User Id=SA;Password=yourWeak(!)Password;Database=Orleans";
            o.Invariant = "System.Data.SqlClient";
        });
        //orleans.UseRedisClustering(options => options.ConfigurationOptions = ConfigurationOptions.Parse("redis:6379"));    
    }

    orleans.UseDashboard(o =>
    {
        o.HostSelf = true;
        o.Port = 8888;
    });
});

builder.Services.AddGrpc();
builder.Services.AddHostedService<WorkerService>();
var app = builder.Build();
app.MapGrpcService<ChaosService>();
await app.StartAsync();
await app.WaitForShutdownAsync();
