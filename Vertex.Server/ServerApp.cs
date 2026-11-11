using System.Reflection;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using EntexInteractive.Extensions.Diagnostics;
using EntexInteractive.Extensions.Globalization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Vertex.Core.Net;
using Vertex.Server.Branches;
using Vertex.Server.Database;
using Vertex.Server.Diagnostics;
using Vertex.Server.FileServices;
using Vertex.Server.Projects;
using Vertex.Server.Services;
using Vertex.Server.Storage;

namespace Vertex.Server
{
    /// <summary>
    /// Provides the application entry point for the server host.
    /// </summary>
    public static class ServerApp
    {
        /// <summary>
        /// Configures and starts the server application, including hosting, middleware, API endpoints, gRPC services, static file handling, and runtime integrations.
        /// </summary>
        /// <param name="args">Command-line arguments provided to the application at startup.</param>
        /// <return>A task that represents the asynchronous operation of building and running the server application.</return>
        public static async Task Main(string[] args)
        {
            CommandLineArguments arguments = new(args);
            
            
            ServerSettings settings = ServerSettingsManager.Load();
            AssemblyName assembly = Assembly.GetExecutingAssembly().GetName();

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
            });
            
            
            builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration));
            Log.Information($"{assembly.Name} [Version {assembly.Version}]");
            
            if (string.IsNullOrWhiteSpace(settings.Storage.Bucket))
                throw new InvalidOperationException("storage.bucket must be set in server.yml.");

            if (string.IsNullOrWhiteSpace(settings.Storage.AccessKey) != string.IsNullOrWhiteSpace(settings.Storage.SecretKey))
                throw new InvalidDataException("storage.accessKey and storage.secretKey must either both be set or both be empty.");

            string region = settings.Storage.Region;
            string serviceUrl = settings.Storage.Endpoint;
            builder.Services.AddSingleton<IAmazonS3>(_ =>
            {
                AmazonS3Config s3Config = new() { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
                s3Config.ServiceURL = serviceUrl;
                s3Config.ForcePathStyle = true;

                AWSCredentials? credentials = string.IsNullOrWhiteSpace(settings.Storage.AccessKey) ? null : new BasicAWSCredentials(settings.Storage.AccessKey, settings.Storage.SecretKey);
                return credentials is null ? new AmazonS3Client(s3Config) : new AmazonS3Client(credentials, s3Config);
            });

            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton<IContentStore, S3ContentStore>();
            builder.Services.AddSingleton<TransferBudget>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                // Larger HTTP/2 flow-control windows let a single upload stream fill high-latency links;
                // per-stream throughput is bounded by window size / round-trip time.
                options.Limits.Http2.InitialStreamWindowSize = FileTransferLimits.StreamWindowSize;
                options.Limits.Http2.InitialConnectionWindowSize = FileTransferLimits.ConnectionWindowSize;

                int httpPort = Convert.ToInt32(Environment.GetEnvironmentVariable("Http_Port") ?? "5000");
                options.ListenAnyIP(httpPort, listenOptions =>
                {
                    // ALPN selects HTTP/1.1 for regular HTTP requests and HTTP/2 for gRPC.
                    // TLS is required because both protocols cannot be negotiated over one
                    // clear-text endpoint.
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });

            // Database services
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            builder.Services.AddScoped<PostgresDatabase>();
            builder.Services.AddScoped<DapperContext>();

            builder.Services.AddScoped<BranchesRepository>();
            builder.Services.AddScoped<DiagnosticRepository>();
            builder.Services.AddScoped<ProjectsRepository>();
            
            // Caching services
            builder.Services.AddSingleton<CacheContext>();
            builder.Services.AddHostedService<RedisSubscriber>();
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = Environment.GetEnvironmentVariable("RedisConnectionString") ?? "redis:6379";
            });

            builder.Services.AddMemoryCache();
            builder.Services.AddHybridCache(options =>
            {
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    LocalCacheExpiration = TimeSpan.FromMinutes(5),
                    Expiration = TimeSpan.FromMinutes(15)
                };
            });

            // Background Services
            builder.Services.AddSingleton<ProcessMonitor>();
            builder.Services.AddScoped<DiagnosticMiddleware>();

            // Add services to the container.
            builder.Services.AddGrpc(options =>
            {
                // Transfers are streamed in bounded messages; file size is not tied to a gRPC message limit.
                options.MaxReceiveMessageSize = FileTransferLimits.MaximumMessageSize;
                options.MaxSendMessageSize = FileTransferLimits.MaximumMessageSize;
            });
            
            builder.Services.AddGrpcReflection();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.Converters.Add(new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() });
            });
            
            builder.Services.AddOpenApi();
            builder.Services.AddSwaggerGen(gen =>
            {
                string xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                gen.IncludeXmlComments(xmlPath);

                gen.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Description = "For api bearer access.",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.Http,
                    BearerFormat = "JWT",
                    Scheme = "Bearer"
                });

                gen.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            },
                            //Scheme = "oauth2",
                            //Name = "Bearer",
                            //In = ParameterLocation.Header
                        },
                        new List<string>()
                    }
                });
            });

            WebApplication app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapGrpcReflectionService();
                //app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // Middleware
            app.UseMiddleware<DiagnosticMiddleware>();

            // Serve only the generated React files, even when development static assets are registered.
            using PhysicalFileProvider reactFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(app.Environment.WebRootPath);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = reactFiles });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = reactFiles });
            
            app.MapGrpcService<GreeterService>();
            app.MapGrpcService<FileTransferService>();
            
            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();
            await app.RunAsync();
        }
    }
}
