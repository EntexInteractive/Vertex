using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Vertex.Server.Services;

namespace Vertex.Server
{
    public static class ServerApp
    {
        public static async Task Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration));
            builder.Services.AddSpaStaticFiles(config => { config.RootPath = "wwwroot"; });
            
            builder.WebHost.UseUrls();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(Convert.ToInt32(Environment.GetEnvironmentVariable("Http_Port") ?? "5000"), listenOptions =>
                { 
                    listenOptions.Protocols = HttpProtocols.Http1;
                });

                options.ListenAnyIP(Convert.ToInt32(Environment.GetEnvironmentVariable("Http2_Port") ?? "5002"), listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2; 
                });
            });
            
            // Caching services
            builder.Services.AddMemoryCache();

            // Add services to the container.
            builder.Services.AddGrpc();
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
            
            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();
            await app.RunAsync();
        }
    
        static bool IsSpaRequest(HttpContext context)
        {
            return !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        }
    }
}