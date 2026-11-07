using System.Reflection;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using EntexInteractive.Extensions.Globalization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Vertex.Server.Services;

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
            CommandLineArguments arguments = new CommandLineArguments(args);

            ServerSettings settings = ServerSettingsManager.Load();
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration));
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

                AWSCredentials? credentials = string.IsNullOrWhiteSpace(settings.Storage.AccessKey)
                    ? null
                    : new BasicAWSCredentials(settings.Storage.AccessKey, settings.Storage.SecretKey);

                return credentials is null ? new AmazonS3Client(s3Config) : new AmazonS3Client(credentials, s3Config);
            });

            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton<IFileStorage, S3FileStorage>();
            builder.Services.AddSpaStaticFiles(config => { config.RootPath = "wwwroot"; });
            builder.WebHost.ConfigureKestrel(options =>
            {
                int httpPort = Convert.ToInt32(Environment.GetEnvironmentVariable("Http_Port") ?? "5000");
                if (Environment.GetEnvironmentVariable("VERTEX_GUI") == "1")
                {
                    options.ListenLocalhost(httpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
                }
                else
                {
                    options.ListenAnyIP(httpPort, listenOptions =>
                    {
                        // ALPN selects HTTP/1.1 for regular HTTP requests and HTTP/2 for gRPC.
                        // TLS is required because both protocols cannot be negotiated over one
                        // clear-text endpoint.
                        listenOptions.UseHttps();
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                    });
                }
            });
            
            // Caching services
            builder.Services.AddMemoryCache();

            // Add services to the container.
            builder.Services.AddGrpc(options =>
            {
                // Transfers are streamed in bounded messages; file size is not tied to a gRPC message limit.
                options.MaxReceiveMessageSize = 1024 * 1024;
                options.MaxSendMessageSize = 1024 * 1024;
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

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseWhen(IsSpaRequest, config => config.UseSpaStaticFiles());
            
            app.MapGrpcService<GreeterService>();
            app.MapGrpcService<FileService>();
            
            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();
            await app.RunAsync();
        }

        private static bool IsSpaRequest(HttpContext context)
        {
            return !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        }
    }
}
