// Copyright 2026 Entex Interactive

using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Photino.NET;
using Vertex.Gui.Hosting;

namespace Vertex.Gui
{
    /// <summary>
    /// Starts the local API (ASP.NET Core on loopback, which also serves the embedded web build) and opens the
    /// Photino window on it. The UI calls C# with plain HTTP: see <c>Vertex.Gui/ReadMe.md</c>.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The Vite dev server (<c>npm run dev</c> in Vertex.Web). With <c>-debug</c> the window loads it for hot
        /// reload and Vite proxies <c>/api</c> to <see cref="DevApiPort"/>.
        /// </summary>
        private const string ViteDevServer = "http://localhost:5173";

        /// <summary>
        /// The fixed API port in <c>-debug</c> runs, matching the proxy in <c>Vertex.Web/vite.config.ts</c>.
        /// <c>VX_GUI_PORT</c> overrides it.
        /// </summary>
        private const int DevApiPort = 5180;

        [STAThread]
        public static void Main(string[] args)
        {
            bool debug = args.Any(arg => arg is "-debug" or "--debug");

            WebApplication app = BuildApp(args, debug);
            app.StartAsync().GetAwaiter().GetResult();

            HostIdentity identity = app.Services.GetRequiredService<HostIdentity>();
            identity.SetPort(BoundPort(app));

            string pageOrigin = identity.Origin;
            if (debug && IsListening(ViteDevServer))
            {
                pageOrigin = ViteDevServer;
                identity.AllowOrigin(ViteDevServer);
            }
            else if (debug)
            {
                app.Logger.LogWarning("The Vite dev server isn't running at {Url}, so the embedded UI is shown. Run 'npm run dev' in Vertex.Web for hot reload.", ViteDevServer);
            }

            // The fragment never leaves the window, so the token isn't logged or sent anywhere; the page reads it once.
            string pageUrl = $"{pageOrigin}/#token={identity.Token}";
            app.Logger.LogInformation("Serving the Vertex UI from {Origin}", pageOrigin);

            PhotinoWindow window = new PhotinoWindow()
                .SetTitle("Vertex")
                .SetUseOsDefaultSize(false)
                .SetSize(new Size(1280, 800))
                .Center()
                .SetResizable(true)
                .SetDevToolsEnabled(debug)
                .SetContextMenuEnabled(debug)
                .Load(pageUrl);

            app.Services.GetRequiredService<DesktopShell>().Attach(window);
            window.WaitForClose();
            app.StopAsync().GetAwaiter().GetResult();
        }

        private static WebApplication BuildApp(string[] args, bool debug)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            int port = debug ? DevPort() : 0;
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, port));

            builder.Services.AddSingleton(new HostIdentity(debug ? DevToken() : null));
            builder.Services.AddSingleton<HostEvents>();
            builder.Services.AddSingleton<ServerSession>();
            builder.Services.AddSingleton<WorkspaceRegistry>();
            builder.Services.AddSingleton<OperationRunner>();
            builder.Services.AddSingleton<DesktopShell>();
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ServerErrorHandler>();
            builder.Services.AddOpenApi();
            // The OpenAPI schemas are generated from these options, so they must match the controllers' below.
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            });
            builder.Services.AddControllers(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesAttribute("application/json")))
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                    // Numbers stay numbers in the OpenAPI document (and so in TypeScript), not "integer or string".
                    options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
                });

            WebApplication app = builder.Build();

            app.UseExceptionHandler();
            app.UseStatusCodePages();
            app.UseMiddleware<HostSecurityMiddleware>();

            ManifestEmbeddedFileProvider files = new(Assembly.GetExecutingAssembly(), "Resources/wwwroot");
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

            app.MapControllers();
            if (debug) app.MapOpenApi("/api/openapi.json");
            app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "The desktop app has no such API. Rebuild Vertex.Gui if the UI is newer than the host."));
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });

            return app;
        }

        private static int DevPort()
        {
            return int.TryParse(Environment.GetEnvironmentVariable("VX_GUI_PORT"), out int port) && port is > 0 and < 65536 ? port : DevApiPort;
        }

        /// <summary>
        /// <c>VX_GUI_TOKEN</c> fixes the token in <c>-debug</c> runs, so scripts and headless browsers can call the API
        /// (open <c>http://127.0.0.1:5180/#token=&lt;token&gt;</c>). It must be at least 32 characters.
        /// </summary>
        private static string? DevToken()
        {
            string? token = Environment.GetEnvironmentVariable("VX_GUI_TOKEN");
            return token is { Length: >= 32 } && token.All(char.IsAsciiLetterOrDigit) ? token : null;
        }

        private static int BoundPort(WebApplication app)
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Uri(address).Port;
        }

        private static bool IsListening(string url)
        {
            Uri uri = new(url);
            try
            {
                using TcpClient client = new();
                return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
            }
            catch (Exception ex) when (ex is SocketException or AggregateException)
            {
                return false;
            }
        }
    }
}
