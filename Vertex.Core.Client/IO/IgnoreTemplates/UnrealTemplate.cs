// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.IO.IgnoreTemplates
{
    /// <summary>
    /// Unreal Engine project: keeps source, config and content; skips everything the engine or IDE regenerates.
    /// Patterns without a leading slash match at any depth, so plugins' build output is covered too.
    /// </summary>
    public sealed class UnrealTemplate : IgnoreTemplate
    {
        public override string Name => "unreal";

        public override string DisplayName => "Unreal Engine";

        public override IReadOnlyList<string> Lines { get; } =
        [
            "# Vertex ignore rules for Unreal Engine. Same syntax as .gitignore.",
            "",
            "# Build output (also inside Plugins/)",
            "Binaries/",
            "Intermediate/",
            "",
            "# Local caches, logs, autosaves and crash dumps",
            "Saved/",
            "DerivedDataCache/",
            "",
            "# Generated IDE project files",
            "*.sln",
            "*.slnx",
            "*.xcworkspace",
            "*.code-workspace",
            ".vs/",
            ".vscode/",
            ".idea/",
            "",
            "# IDE databases and user settings",
            "*.VC.db",
            "*.opensdf",
            "*.sdf",
            "*.suo",
            "*.user",
            "",
            "# OS files",
            ".DS_Store",
            "Thumbs.db",
            "Desktop.ini"
        ];

        public override bool Detect(string root)
        {
            return HasFile(root, "*.uproject");
        }
    }
}