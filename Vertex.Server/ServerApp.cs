using System.Reflection;
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
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration));
            builder.Services.AddSpaStaticFiles(config => { config.RootPath = "wwwroot"; });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(Convert.ToInt32(Environment.GetEnvironmentVariable("Https_Port") ?? "5000"), listenOptions =>
                {
                    // ALPN selects HTTP/1.1 for regular HTTP requests and HTTP/2 for gRPC.
                    // TLS is required because both protocols cannot be negotiated over one
                    // clear-text endpoint.
                    listenOptions.UseHttps();
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                });
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
