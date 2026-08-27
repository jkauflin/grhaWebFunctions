using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Authentication;


var builder = FunctionsApplication.CreateBuilder(args);

//services.AddApplicationInsightsTelemetryWorkerService();
//services.ConfigureFunctionsApplicationInsights();

builder.ConfigureFunctionsWebApplication();   // <-- missing in yours

if (!string.IsNullOrEmpty(
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

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
