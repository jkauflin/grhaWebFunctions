/*==============================================================================
(C) Copyright 2026 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Entry point for the Azure Function App.  This is where the 
                function app is configured, and the services are registered 
                with the DI container.
--------------------------------------------------------------------------------
Modification History
2026-09-11 JJK  Added DI for a Cosmos DB client and DbCommon class.  Cosmos DB 
                client is configured to use a managed identity in production, 
                and a default credential in development (so connection string
                is no longer needed)
2026-10-20 JJK  Added DI for a BlobServiceClient, configured to use a managed 
                identity in production, and a default credential in development
================================================================================*/
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Azure.Cosmos;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Authentication;
using grhaWebFunctions;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

if (!string.IsNullOrEmpty(
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

// DI for classes that can be singletons and used by multiple functions in the function app
builder.Services.AddSingleton<AuthorizationCheck>();
builder.Services.AddSingleton<CommonUtil>();
builder.Services.AddSingleton<HoaDbCommon>();

// DI for Cosmos DB Client
builder.Services.AddSingleton<CosmosClient>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    var endpoint =
        builder.Configuration["API_COSMOS_DB_ENDPOINT"]
        ?? throw new InvalidOperationException(
            "API_COSMOS_DB_ENDPOINT is not configured.");

    TokenCredential credential;

    if (builder.Environment.IsDevelopment())
    {
        credential = new DefaultAzureCredential();
    }
    else
    {
        credential = new ManagedIdentityCredential(   
            ManagedIdentityId.SystemAssigned);
    }

    return new CosmosClient(
        endpoint,
        credential);
});

// DI for Storage Blob Client
builder.Services.AddSingleton<BlobServiceClient>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    var storageAccountName =
        builder.Configuration["API_STORAGE_ACCOUNT_NAME"]
        ?? throw new InvalidOperationException(
            "API_STORAGE_ACCOUNT_NAME is not configured.");

    TokenCredential credential;

    if (builder.Environment.IsDevelopment())
    {
        credential = new DefaultAzureCredential();
    }
    else
    {
        credential = new ManagedIdentityCredential(   
            ManagedIdentityId.SystemAssigned);
    }

    return new BlobServiceClient(
        new Uri(
            $"https://{storageAccountName}.blob.core.windows.net"),
        credential);
});

// DI for PaypalServerSdkClient
builder.Services.AddSingleton<PaypalServerSdkClient>(sp =>
{
    var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

    var clientId = configuration["PAYPAL_CLIENT_ID"];
    var clientSecret = configuration["PAYPAL_CLIENT_SECRET"];
    var clientEnvironment = configuration["PAYPAL_ENVIRONMENT"];

    var env = clientEnvironment?.Equals(
        "Production",
        StringComparison.OrdinalIgnoreCase) == true
        ? PaypalServerSdk.Standard.Environment.Production
        : PaypalServerSdk.Standard.Environment.Sandbox;

    return new PaypalServerSdkClient.Builder()
        .ClientCredentialsAuth(
            new ClientCredentialsAuthModel.Builder(
                clientId,
                clientSecret)
            .Build()
        )
        .Environment(env)
        .Build();
});

builder.Build().Run();
