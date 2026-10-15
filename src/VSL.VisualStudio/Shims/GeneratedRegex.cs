using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using VSL.Claude;
using VSL.ViewModels;

namespace System.Text.RegularExpressions
{
    /// <summary>
    /// .NET Framework has no Regex source generator: the shared files keep their [GeneratedRegex] methods, and the
    /// implementations below build each one from its attribute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class GeneratedRegexAttribute(string pattern, RegexOptions options = RegexOptions.None) : Attribute
    {
        public string Pattern { get; } = pattern;

        public RegexOptions Options { get; } = options;
    }
}

namespace VSL.VisualStudio.Shims
{
    internal static class GeneratedRegexes
    {
        static readonly ConcurrentDictionary<(Type, string), Regex> Cache = new();

        public static Regex For(Type type, string method) => Cache.GetOrAdd((type, method), key =>
        {
            var attribute = key.Item1.GetMethod(key.Item2, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetCustomAttribute<GeneratedRegexAttribute>()
                ?? throw new InvalidOperationException($"{key.Item1.Name}.{key.Item2} has no [GeneratedRegex].");
            return new Regex(attribute.Pattern, attribute.Options | RegexOptions.Compiled);
        });
    }
}

namespace VSL.Claude
{
    public static partial class ClaudeCli
    {
        private static partial Regex NonAlphanumeric() => VisualStudio.Shims.GeneratedRegexes.For(typeof(ClaudeCli), nameof(NonAlphanumeric));
    }

    public static partial class ClaudeHistory
    {
        private static partial Regex ContextPattern() => VisualStudio.Shims.GeneratedRegexes.For(typeof(ClaudeHistory), nameof(ContextPattern));
    }
}

namespace VSL.ViewModels
{
    public sealed partial class ClaudeViewModel
    {
        private static partial Regex FileLinkLine() => VisualStudio.Shims.GeneratedRegexes.For(typeof(ClaudeViewModel), nameof(FileLinkLine));

        private static partial Regex FileMention() => VisualStudio.Shims.GeneratedRegexes.For(typeof(ClaudeViewModel), nameof(FileMention));
    }
}
