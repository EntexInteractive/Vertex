using System.Drawing;
using System.Text.Json;
using System.Text;
using EntexInteractive.Extensions.Globalization;
using Photino.NET;
using Photino.NET.Server;

namespace Vertex.Gui
{
    //NOTE: To hide the console window, go to the project properties and change the Output Type to Windows Application.
// Or edit the .csproj file and change the <OutputType> tag from "WinExe" to "Exe".

    class Program
    {
        public static bool IsDebugMode = false; //serve files from asp.net runtime
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        [STAThread]
        static void Main(string[] args)
        {
            PhotinoServer.CreateStaticFileServer(args, out string baseUrl).RunAsync();

            // The appUrl is set to the local development server when in debug mode.
            // This helps with hot reloading and debugging.
            string appUrl = IsDebugMode ? "http://localhost:3000" : $"{baseUrl}/index.html";
            Console.WriteLine($"Serving React app at {appUrl}");

            // Window title declared here for visibility
            string windowTitle = "Vertex Desktop";

            // Creating a new PhotinoWindow instance with the fluent API
            PhotinoWindow window = new PhotinoWindow()
                .SetTitle(windowTitle)
                .SetUseOsDefaultSize(false)
                .SetSize(new Size(2048, 1024))
                // Resize to a percentage of the main monitor work area
                //.Resize(50, 50, "%")
                .SetUseOsDefaultSize(false)
                .SetSize(new Size(800, 600))
                // Center window in the middle of the screen
                .Center()
                // Users can resize windows by default.
                // Let's make this one fixed instead.
                .SetResizable(true)
                .RegisterCustomSchemeHandler("app", (object sender, string scheme, string url, out string contentType) =>
                {
                    contentType = "text/javascript";
                    return new MemoryStream(Encoding.UTF8.GetBytes(@"
                        (() =>{
                            window.setTimeout(() => {
                                alert(`🎉 Dynamically inserted JavaScript.`);
                            }, 1000);
                        })();
                    "));
                })
                // Most event handlers can be registered after the
                // PhotinoWindow was instantiated by calling a registration 
                // method like the following RegisterWebMessageReceivedHandler.
                // This could be added in the PhotinoWindowOptions if preferred.
                .RegisterWebMessageReceivedHandler((sender, message) => HandleUiMessage((PhotinoWindow)sender!, message))
                .Load(appUrl); // Can be used with relative path strings or "new URI()" instance to load a website.

            window.WaitForClose(); // Starts the application event loop
        }

        private static void HandleUiMessage(PhotinoWindow? window, string message)
        {
            UiRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<UiRequest>(message, JsonOptions);
            }
            catch (JsonException)
            {
                return;
            }

            if (request is null || request.Type != "request" || string.IsNullOrWhiteSpace(request.Id))
                return;

            object? result = null;
            string? error = null;
            try
            {
                // Keep this switch as the explicit allowlist of methods the UI may call.
                result = request.Method switch
                {
                    "app.getGreeting" => GetGreeting(request.Payload),
                    _ => throw new InvalidOperationException($"Unknown backend method: {request.Method}")
                };
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            window?.SendWebMessage(JsonSerializer.Serialize(new UiResponse("response", request.Id, error is null, result, error), JsonOptions));
        }

        private static string GetGreeting(JsonElement payload)
        {
            var name = payload.ValueKind == JsonValueKind.Object &&
                       payload.TryGetProperty("name", out var nameValue) &&
                       nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;

            return $"Hello, {(!string.IsNullOrWhiteSpace(name) ? name : "there")}! This response came from C#.";
        }

        private sealed record UiRequest(string Type, string Id, string Method, JsonElement Payload);
        private sealed record UiResponse(string Type, string Id, bool Success, object? Result, string? Error);
    }
}
