using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Models.Scripts;
using System.Collections.Concurrent;

namespace Skua.Core.Scripts;

public sealed class ScriptPathResolver : IScriptPathResolver
{
    private readonly ICustomScriptService _customScripts;
    private readonly string _repositoryRoot = Canonicalize(ClientFileSources.SkuaScriptsDIR);
    private readonly ConcurrentDictionary<(string? ownerFile, string specifier), ScriptPathResolution> _resolutionCache = new();

    public ScriptPathResolver(ICustomScriptService customScripts)
    {
        _customScripts = customScripts;
    }

    public ScriptPathResolution Resolve(string? ownerFile, string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier))
            return new(null, Array.Empty<string>());

        var cacheKey = (ownerFile, specifier);
        if (_resolutionCache.TryGetValue(cacheKey, out var cachedResolution))
            return cachedResolution;

        string normalizedSpecifier = specifier.Trim().Replace('/', Path.DirectorySeparatorChar);
        List<string> candidates = new();

        if (Path.IsPathRooted(normalizedSpecifier))
        {
            candidates.Add(normalizedSpecifier);
        }
        else if (normalizedSpecifier.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 normalizedSpecifier.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
        }
        else if (normalizedSpecifier.StartsWith($"Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            string subPath = normalizedSpecifier[("Scripts".Length + 1)..];
            candidates.Add(Path.Combine(_repositoryRoot, subPath));

            int firstSlash = subPath.IndexOf(Path.DirectorySeparatorChar);
            if (firstSlash > 0)
            {
                string folderSegment = subPath[..firstSlash];
                string relativeInFolder = subPath[(firstSlash + 1)..];

                // Check repository root with common GitHub ZIP branch suffixes
                candidates.Add(Path.Combine(_repositoryRoot, $"{folderSegment}-main", relativeInFolder));
                candidates.Add(Path.Combine(_repositoryRoot, $"{folderSegment}-master", relativeInFolder));

                // Check relative to owner file directory and parent directory (e.g. scripts inside subfolders)
                if (!string.IsNullOrWhiteSpace(ownerFile))
                {
                    string? ownerDir = Path.GetDirectoryName(Canonicalize(ownerFile));
                    if (!string.IsNullOrEmpty(ownerDir))
                    {
                        candidates.Add(Path.Combine(ownerDir, relativeInFolder));
                        string? parentDir = Path.GetDirectoryName(ownerDir);
                        if (!string.IsNullOrEmpty(parentDir) && !parentDir.Equals(_repositoryRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(Path.Combine(parentDir, relativeInFolder));
                        }
                    }
                }

                foreach (string customRoot in _customScripts.GetSearchRoots())
                {
                    if (Directory.Exists(customRoot))
                    {
                        candidates.Add(Path.Combine(customRoot, subPath));
                        candidates.Add(Path.Combine(customRoot, $"{folderSegment}-main", relativeInFolder));
                        candidates.Add(Path.Combine(customRoot, $"{folderSegment}-master", relativeInFolder));
                    }
                }
            }
            else
            {
                foreach (string customRoot in _customScripts.GetSearchRoots())
                {
                    if (Directory.Exists(customRoot))
                        candidates.Add(Path.Combine(customRoot, subPath));
                }
            }

            AddOwnerRelativeCandidate(candidates, ownerFile, subPath);
        }
        else if (_customScripts.IsCustomPath(ownerFile ?? string.Empty))
        {
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
            candidates.Add(Path.Combine(_repositoryRoot, normalizedSpecifier));
        }
        else
        {
            candidates.Add(Path.Combine(_repositoryRoot, normalizedSpecifier));
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
        }

        if (normalizedSpecifier.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, normalizedSpecifier));
            string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
                candidates.Add(Path.Combine(runtimeDir, normalizedSpecifier));
        }

        List<string> existing = candidates
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        // Scripts are written on Windows, where paths ignore case ("Story/AgeofRuin" finds
        // "Story/AgeOfRuin"). On case-sensitive file systems (Linux, Android) look again,
        // matching each path segment without regard to case.
        if (existing.Count == 0 && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            existing = candidates
                .Select(Canonicalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(FindIgnoringCase)
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct()
                .ToList();
        }

        ScriptPathResolution resolution = new(existing.FirstOrDefault(), existing);
        if (resolution.SelectedPath != null)
            _resolutionCache[cacheKey] = resolution;

        return resolution;
    }

    private static void AddOwnerRelativeCandidate(List<string> candidates, string? ownerFile, string specifier)
    {
        if (!string.IsNullOrWhiteSpace(ownerFile))
            candidates.Add(Path.Combine(Path.GetDirectoryName(Canonicalize(ownerFile))!, specifier));
    }

    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Resolve <paramref name="path"/> segment by segment, ignoring case. Null if nothing matches.</summary>
    private static string? FindIgnoringCase(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            string current = root;
            string[] parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string exact = Path.Combine(current, parts[i]);
                bool last = i == parts.Length - 1;
                if (last ? File.Exists(exact) : Directory.Exists(exact))
                {
                    current = exact;
                    continue;
                }
                if (!Directory.Exists(current))
                    return null;
                string? match = (last ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current))
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return null;
                current = match;
            }
            return File.Exists(current) ? current : null;
        }
        catch
        {
            return null;
        }
    }
}
