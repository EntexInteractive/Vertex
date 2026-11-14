using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Amazon;
using Grpc.AspNetCore.Web;
using Amazon.Runtime;
using Amazon.S3;
using EntexInteractive.Extensions.Diagnostics;
using EntexInteractive.Extensions.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Vertex.Core.Net;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Branches;
using Vertex.Server.Database;
using Vertex.Server.Diagnostics;
using Vertex.Server.FileServices;
using Vertex.Server.Groups;
using Vertex.Server.Projects;
using Vertex.Server.Services;
using Vertex.Server.Storage;
using Vertex.Server.Users;

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


            ServerSettings settings = ServerSettings.FromEnvironment();
            AssemblyName assembly = Assembly.GetExecutingAssembly().GetName();

            // Resolve appsettings.json and wwwroot next to the executable rather than the working directory, so a
            // standalone server logs and serves the UI correctly no matter where it's started from.
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
            });
            
            
            builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration));
            Log.Information($"{assembly.Name} [Version {assembly.Version}]");
            
            string region = settings.Storage.Region;
            string serviceUrl = settings.Storage.Endpoint;
            builder.Services.AddSingleton<IAmazonS3>(_ =>
            {
                AmazonS3Config s3Config = new() { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
                s3Config.ServiceURL = serviceUrl;
                s3Config.ForcePathStyle = true;

                AWSCredentials? credentials = settings.Storage.AccessKey is null ? null : new BasicAWSCredentials(settings.Storage.AccessKey, settings.Storage.SecretKey);
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

                options.ListenAnyIP(settings.HttpPort, listenOptions =>
                {
                    if (settings.UseTls)
                    {
                        // With TLS, ALPN picks HTTP/1.1 or HTTP/2 per connection, so proxies that reach the origin
                        // over HTTP/1.1 (Cloudflare's default) work alongside gRPC over HTTP/2.
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        listenOptions.UseHttps(LoadCertificate(settings.TlsCertificatePath!, settings.TlsKeyPath!));
                    }
                    else
                    {
                        // Without TLS there is no way to negotiate, and Kestrel answers HTTP/2 requests with
                        // HTTP_1_1_REQUIRED if HTTP/1.1 is also enabled. gRPC needs HTTP/2, so serve only that.
                        listenOptions.Protocols = HttpProtocols.Http2;
                    }
                });
            });

            // Database services
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            builder.Services.AddSingleton<DatabaseMigrator>();
            builder.Services.AddScoped<PostgresDatabase>();
            builder.Services.AddScoped<DapperContext>();

            builder.Services.AddScoped<BranchesRepository>();
            builder.Services.AddScoped<DiagnosticRepository>();
            builder.Services.AddScoped<ProjectsRepository>();
            builder.Services.AddScoped<UsersRepository>();
            builder.Services.AddScoped<GroupsRepository>();
            builder.Services.AddScoped<TokensRepository>();
            builder.Services.AddScoped<AccessService>();

            // Authentication: opaque bearer tokens from 'vx login'. Every endpoint requires a logged-in user unless it
            // opts out with [AllowAnonymous] (server info, login), which covers REST and gRPC alike.
            builder.Services.AddSingleton<LoginThrottle>();
            builder.Services.AddHostedService<AdminBootstrapper>();
            builder.Services.AddAuthentication(VertexClaims.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(VertexClaims.Scheme, null);

            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            });
            
            // Caching services
            builder.Services.AddSingleton<CacheContext>();
            builder.Services.AddHostedService<RedisSubscriber>();
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = settings.RedisConnectionString;
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
                    BearerFormat = "vxp_ token from vx login",
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

            // Bring the database schema up to date before any hosted service runs or any request is accepted. On
            // failure, exit non-zero so a container restart policy retries once the cause is fixed.
            try
            {
                await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
            }
            catch (InvalidOperationException ex)
            {
                Log.Fatal(ex, "{Message}", ex.Message);
                await Log.CloseAndFlushAsync();
                Environment.ExitCode = 1;
                return;
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi().AllowAnonymous();
                app.MapGrpcReflectionService().AllowAnonymous();
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

            // Accept gRPC-Web as well as native gRPC. gRPC-Web carries the call status in the response body instead of
            // HTTP/2 trailers, which some proxies (Cloudflare Tunnel) drop.
            app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGrpcService<GreeterService>();
            app.MapGrpcService<FileTransferService>();
            
            app.UseHttpsRedirection();
            app.MapControllers();
            await app.RunAsync();
        }

        /// <summary>
        /// Loads a PEM certificate and key, e.g. a Cloudflare Origin CA certificate.
        /// </summary>
        private static X509Certificate2 LoadCertificate(string certificatePath, string keyPath)
        {
            // A key loaded from PEM is ephemeral, which Windows' TLS stack can't use; round-tripping through PKCS#12
            // gives it a persisted key. Harmless on Linux.
            using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
    }
}
