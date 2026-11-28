using System.IO.Enumeration;

namespace Cloudreve.Desktop;

internal sealed class PairIgnoreRules
{
    private sealed record Rule(string[] Parts, bool Anchored, bool DirectoriesOnly, bool Include);

    private readonly List<Rule> rules = [];

    // Metadata of other sync tools and of the system, ignored in every pair before the user's rules, which can
    // re-include any of it with "!". Ignored paths are never deleted on either side; they only stop syncing.
    internal const string BuiltIn = """
        .stfolder/
        .stversions/
        .syncthing.*.tmp
        Thumbs.db
        ehthumbs.db
        desktop.ini
        ~$*
        .~lock.*#
        """;

    internal PairIgnoreRules(string text)
    {
        if (text.Length > 32768)
        {
            throw new InvalidDataException(UiText.Get("ignoreRules.tooLong"));
        }

        foreach (string line in (BuiltIn + "\n" + text).ReplaceLineEndings("\n").Split('\n'))
        {
            string pattern = line.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }

            bool include = pattern.StartsWith('!');
            if (include)
            {
                pattern = pattern[1..];
            }

            bool directoriesOnly = pattern.EndsWith('/');
            bool rooted = pattern.StartsWith('/');
            string[] parts = pattern.Trim('/').Split('/');
            if (parts.Length > 64 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny(['\\', '[', ']', '\0']) >= 0))
            {
                throw new InvalidDataException(UiText.Get("ignoreRules.invalid"));
            }

            rules.Add(new Rule(parts, rooted || parts.Length > 1, directoriesOnly, include));
        }
    }

    internal bool Excludes(string key, bool directory = false)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        string[] names = key.Split('/');
        for (int depth = 1; depth <= names.Length; depth++)
        {
            bool ignored = false;
            foreach (var rule in rules)
            {
                if (rule.DirectoriesOnly && depth == names.Length && !directory)
                {
                    continue;
                }

                bool matches = rule.Anchored ? MatchPath(rule.Parts, names, depth) : FileSystemName.MatchesSimpleExpression(rule.Parts[0], names[depth - 1], true);
                if (matches)
                {
                    ignored = !rule.Include;
                }
            }

            if (ignored)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchPath(string[] pattern, string[] names, int length)
    {
        // Keep all reachable path positions. Consecutive ** components never cause exponential backtracking.
        var positions = new bool[length + 1];
        positions[0] = true;
        foreach (string part in pattern)
        {
            if (part == "**")
            {
                for (int i = 1; i <= length; i++)
                {
                    positions[i] |= positions[i - 1];
                }
            }
            else
            {
                for (int i = length; i > 0; i--)
                {
                    positions[i] = positions[i - 1] && FileSystemName.MatchesSimpleExpression(part, names[i - 1], true);
                }

                positions[0] = false;
            }
        }

        return positions[length];
    }
}