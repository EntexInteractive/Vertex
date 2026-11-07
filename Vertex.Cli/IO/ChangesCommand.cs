// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Contexts;
using Vertex.Core.Data;

namespace Vertex.Cli.IO
{
    public class ChangesCommand : BaseIOCommand
    {
        private readonly ClientDbContext _db;

        public ChangesCommand(ClientDbContext db) : base("changes", "Gets a list of changed files.")
        {
            this.SetAction(HandleCommandAsync);
            _db = db;
        }

        private void HandleCommandAsync(ParseResult arg)
        {
            string path = arg.GetValue(PathOption) ?? Directory.GetCurrentDirectory();
            Terminal.WriteLine($"Using path: {path}");
        }
    }
}