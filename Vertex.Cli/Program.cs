using System.CommandLine.Help;
using System.Reflection;

namespace Vertex.Cli
{
    internal static class Program
    {
        private static Option<bool> _versionOption = new Option<bool>("--version", ["-v"]) { Description = "Show version information" };

        private static async Task Main(string[] args)
        {
            RootCommand rootCommand = new("Vertex - Command-line interface for interacting with a Vertex server.");
            IEnumerable<Type> types = Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(Command).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(Command));
            foreach (Type type in types) rootCommand.Add((Command)Activator.CreateInstance(type)!);
            rootCommand.SetAction(HandleRootCommand);
            rootCommand.Options.RemoveAt(1);
            rootCommand.Add(_versionOption);
            
            try
            {
                await rootCommand.Parse(args).InvokeAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                throw;
            }
        }

        private static void HandleRootCommand(ParseResult args)
        {
            if (args.GetValue(_versionOption))
            {
                Console.WriteLine(typeof(Program).Assembly.GetName().Version);
                return;
            }
            
            HelpAction helpAction = new HelpAction();
            helpAction.Invoke(args);
        }
    }
}