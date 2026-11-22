using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KeyceCode.Services;

/// <summary>Resolves C# project paths from Visual Studio .sln and .slnx files.</summary>
public static partial class DotNetSolutionService
{
    [GeneratedRegex("^\\s*Project\\(\\\"[^\\\"]+\\\"\\)\\s*=\\s*\\\"[^\\\"]*\\\"\\s*,\\s*\\\"(?<path>[^\\\"]+\\.csproj)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectLineRegex();

    public static IReadOnlyList<string> GetProjects(string solutionPath)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var root = Path.GetDirectoryName(fullPath)!;
        IEnumerable<string> relativePaths;
        if (string.Equals(Path.GetExtension(fullPath), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var xml = XDocument.Load(fullPath, LoadOptions.None);
            relativePaths = xml.Descendants().Where(element => element.Name.LocalName == "Project")
                .Select(element => (string?)element.Attribute("Path"))
                .Where(value => !string.IsNullOrWhiteSpace(value) && Path.GetExtension(value).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(value => value!);
        }
        else
        {
            relativePaths = File.ReadLines(fullPath).Select(line => ProjectLineRegex().Match(line))
                .Where(match => match.Success).Select(match => match.Groups["path"].Value);
        }

        return relativePaths.Select(path => Path.GetFullPath(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
