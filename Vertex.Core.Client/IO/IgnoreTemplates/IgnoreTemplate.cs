// Copyright 2026 Entex Interactive

using System.Reflection;

namespace Vertex.Core.Client.IO.IgnoreTemplates
{
    /// <summary>
    /// A starter <c>.vxignore</c> for a kind of project, written by <c>vx init</c>.
    /// </summary>
    /// <remarks>
    /// To add a template, create a class in this folder that derives from <see cref="IgnoreTemplate"/> and has a
    /// public parameterless constructor. It is picked up automatically; nothing needs registering. When several
    /// templates detect the same folder, the one with the highest <see cref="Priority"/> wins.
    /// </remarks>
    public abstract class IgnoreTemplate
    {
        private static readonly Lazy<IReadOnlyList<IgnoreTemplate>> Templates = new(Discover);

        /// <summary>Every available template, highest priority first.</summary>
        public static IReadOnlyList<IgnoreTemplate> All => Templates.Value;

        /// <summary>Short name used with <c>vx init --template</c>, e.g. <c>unreal</c>.</summary>
        public abstract string Name { get; }

        /// <summary>Human-readable name, e.g. "Unreal Engine".</summary>
        public abstract string DisplayName { get; }

        /// <summary>Breaks ties when more than one template detects a folder. Higher wins.</summary>
        public virtual int Priority => 0;

        /// <summary>The <c>.vxignore</c> lines, comments included.</summary>
        public abstract IReadOnlyList<string> Lines { get; }

        /// <summary>
        /// Returns whether <paramref name="root"/> looks like this kind of project.
        /// </summary>
        public abstract bool Detect(string root);

        /// <summary>
        /// Returns the template with the given <see cref="Name"/> (case-insensitive), or null.
        /// </summary>
        public static IgnoreTemplate? Find(string name)
        {
            return All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the template matching the project in <paramref name="root"/>, or null when none does.
        /// </summary>
        public static IgnoreTemplate? DetectFor(string root)
        {
            return All.FirstOrDefault(t => t.Detect(root));
        }

        /// <summary>
        /// Writes the template to <c>.vxignore</c> in <paramref name="root"/>.
        /// </summary>
        public void WriteTo(string root)
        {
            File.WriteAllLines(Path.Combine(root, IgnoreRules.FileName), Lines);
        }

        /// <summary>
        /// Returns whether <paramref name="root"/> directly contains a file matching <paramref name="pattern"/>.
        /// </summary>
        protected static bool HasFile(string root, string pattern)
        {
            return Directory.Exists(root) && Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();
        }

        private static IReadOnlyList<IgnoreTemplate> Discover()
        {
            return typeof(IgnoreTemplate).Assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(IgnoreTemplate)) && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => (IgnoreTemplate)Activator.CreateInstance(t)!)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }
}