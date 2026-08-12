using System.Reflection;
using Entex.Core.IO;
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
        
            DirectoryInfo dashboardDir = FileSystemUtility.AppDirectory;
            Console.WriteLine($"{dashboardDir.FullName}: {dashboardDir.Exists}");
            if (dashboardDir.Exists)
            {
                builder.Services.AddSpaStaticFiles(config => { config.RootPath = "DashboardApp"; });
            }

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(5001, listenOptions =>
                {
                    listenOptions.UseHttps();
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });

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
            if (dashboardDir.Exists)
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();
                app.UseWhen(IsSpaRequest, config => config.UseSpaStaticFiles());
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapGrpcReflectionService();
                //app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

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