using Api;
using Bogus;
using Confluent.Kafka;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared;

// need to extract this, because logging configuration and tracing/metrics configuration don't share this by themselves
Action<ResourceBuilder> configureResource = resourceBuilder =>
{
    resourceBuilder.AddService(
        serviceName: "Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        serviceInstanceId: Environment.MachineName);
};

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddOpenTelemetry(options =>
{
    var resourceBuilder = ResourceBuilder.CreateDefault();
    configureResource(resourceBuilder);
    options.SetResourceBuilder(resourceBuilder);

    options.IncludeScopes = true;

    // false by default, which means the message wouldn't have the placeholders replaced
    options.IncludeFormattedMessage = true;

    // Allows structured logging state to be parsed.
    options.ParseStateValues = true;

    options.AddOtlpExporter(exporterOptions =>
    {
        exporterOptions.Endpoint =
            builder
                .Configuration
                .GetSection(nameof(OpenTelemetrySettings))
                .Get<OpenTelemetrySettings>()!
                .Endpoint;
    });
});

builder.Services.AddSingleton(new Faker());

builder.Services.AddSingleton(
    builder.Configuration
        .GetSection(nameof(KafkaSettings))
        .Get<KafkaSettings>()!);

builder.Services.AddSingleton(
    builder.Configuration
        .GetSection(nameof(OpenTelemetrySettings))
        .Get<OpenTelemetrySettings>()!);

builder.Services.AddSingleton(s =>
    new ProducerBuilder<Guid, StuffHappened>(
            new ProducerConfig
            {
                BootstrapServers =
                    s.GetRequiredService<KafkaSettings>().BootstrapServers
            })
        .SetKeySerializer(new GuidSerde())
        .SetValueSerializer(new JsonEventSerde<StuffHappened>())
        .Build());

builder.Services.AddSingleton<EventPublisherMetrics>();
builder.Services.AddSingleton<EventPublisher>();

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(configureResource)

    .WithTracing(providerBuilder =>
    {
        providerBuilder
            .AddAspNetCoreInstrumentation()
            .AddSource(nameof(EventPublisher))
            .AddOtlpExporter(options =>
            {
                options.Endpoint =
                    builder
                        .Configuration
                        .GetSection(nameof(OpenTelemetrySettings))
                        .Get<OpenTelemetrySettings>()!
                        .Endpoint;

                options.Protocol = OtlpExportProtocol.Grpc;
            });
    })

    .WithMetrics(metrics =>
    {
        metrics
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddAspNetCoreInstrumentation()

            // Metric boundaries used for the HTTP request duration histogram.
            .AddView(
                "http.server.request.duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new double[]
                    {
                        0,
                        0.005,
                        0.01,
                        0.025,
                        0.05,
                        0.075,
                        0.1,
                        0.25,
                        0.5,
                        0.75,
                        1,
                        2.5,
                        5,
                        7.5,
                        10
                    }
                })

            .AddMeter(
                "System.Runtime",
                "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel",
                EventPublisherMetrics.MeterName)

            // .AddPrometheusExporter()

            .AddOtlpExporter(options =>
            {
                options.Endpoint =
                    builder
                        .Configuration
                        .GetSection(nameof(OpenTelemetrySettings))
                        .Get<OpenTelemetrySettings>()!
                        .Endpoint;

                options.Protocol = OtlpExportProtocol.Grpc;
            });
    });

var app = builder.Build();


// Normal successful request.
// This goes through:
// API -> Kafka -> Worker -> PostgreSQL
app.MapPost(
    "/do-stuff",
    async (EventPublisher eventPublisher, Faker faker) =>
    {
        await eventPublisher.PublishAsync(
            new(Guid.NewGuid(), faker.Hacker.Verb()));

        return TypedResults.NoContent();
    });


// POC-only endpoint.
// This intentionally throws an exception so that we can demonstrate
// a 500 error in Prometheus, Grafana and Tempo.
app.MapPost(
    "/do-stuff/error",
    () =>
    {
        throw new Exception("POC test error");
    });


// from OpenTelemetry.Exporter.Prometheus.AspNetCore
// app.MapPrometheusScrapingEndpoint();

app.Run();
