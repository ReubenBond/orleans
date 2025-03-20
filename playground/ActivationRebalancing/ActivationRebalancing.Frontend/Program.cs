// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.UseOrleansClient(clientBuilder => clientBuilder.UseLocalhostClustering());
builder.Services.AddControllers();

var app = builder.Build();

var options = new DefaultFilesOptions();
options.DefaultFileNames.Clear();
options.DefaultFileNames.Add("index.html");

app.UseDefaultFiles(options);
app.UseStaticFiles();
app.MapControllers();
app.Run();
