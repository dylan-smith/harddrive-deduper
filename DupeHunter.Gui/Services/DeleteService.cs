using System.IO;

namespace DupeHunter.Gui.Services;

/// <inheritdoc cref="IDeleteService"/>
public sealed class DeleteService : IDeleteService
{
    public async Task<DeleteOutcome> DeleteAsync(string fullPath, bool isFolder, CancellationToken ct)
    {
        try
        {
            var deleted = await Task.Run(() =>
            {
                // The \\?\ form works past MAX_PATH regardless of the LongPathsEnabled policy the
                // manifest's longPathAware setting depends on.
                var path = ToExtendedLengthPath(fullPath);
                if (isFolder)
                {
                    Directory.Delete(path, recursive: true);
                    return true;
                }

                // File.Delete is a silent no-op on a missing file; report that case honestly.
                if (!File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            }, ct);

            return new DeleteOutcome(deleted ? DeleteStatus.Deleted : DeleteStatus.AlreadyMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return new DeleteOutcome(DeleteStatus.AlreadyMissing);
        }
        catch (FileNotFoundException)
        {
            return new DeleteOutcome(DeleteStatus.AlreadyMissing);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DeleteOutcome(DeleteStatus.Failed, $"Access denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Most commonly a file open in another process (sharing violation).
            return new DeleteOutcome(DeleteStatus.Locked, ex.Message);
        }
    }

    /// <summary>
    /// The path in \\?\ extended-length form: local paths get the \\?\ prefix and UNC paths become
    /// \\?\UNC\server\share\…; already-prefixed paths pass through.
    /// </summary>
    internal static string ToExtendedLengthPath(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(@"\\?\UNC\", path.AsSpan(2))
            : @"\\?\" + path;
    }
}
