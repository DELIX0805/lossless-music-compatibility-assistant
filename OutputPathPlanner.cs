using System.IO;

namespace LightAudioConverter;

internal static class OutputPathPlanner
{
    internal static IReadOnlyList<string> Create(
        IReadOnlyList<AudioFileItem> files,
        string outputFolder,
        string extension)
    {
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new List<string>(files.Count);
        foreach (var item in files)
        {
            var baseName = Path.GetFileNameWithoutExtension(item.FileName);
            var destination = Candidate(baseName, 1, outputFolder, extension);
            for (var suffix = 2; !IsAvailable(destination, item.FilePath, reservedPaths); suffix++)
                destination = Candidate(baseName, suffix, outputFolder, extension);

            reservedPaths.Add(destination);
            destinations.Add(destination);
        }
        return destinations;
    }

    private static string Candidate(string baseName, int suffix, string outputFolder, string extension) =>
        Path.Combine(outputFolder, suffix == 1 ? baseName + extension : $"{baseName} ({suffix}){extension}");

    private static bool IsAvailable(string path, string sourcePath, IReadOnlySet<string> reservedPaths) =>
        !File.Exists(path)
        && !Directory.Exists(path)
        && !reservedPaths.Contains(path)
        && !path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
}
