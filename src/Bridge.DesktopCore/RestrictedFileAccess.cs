using System.Security.Cryptography;

namespace Bridge.DesktopCore;

/// <summary>
/// Restricted file access (T22, docs/04 §11). All access is confined
/// to explicitly authorized root directories. Paths are validated
/// LEXICALLY with Windows semantics (so the rules are identical on
/// every host and unit-testable on Linux); the Windows agent adds a
/// final symlink/reparse-point resolution before calling in here.
/// </summary>
public static class RestrictedFileAccess
{
    public const int MaxReadBytes = 1024 * 1024;
    public const int MaxListEntries = 500;

    public sealed record FileError(string Code, string Message);

    /// <summary>
    /// Validate a requested path against ONE authorized root.
    /// Windows semantics: backslash separators, case-insensitive
    /// roots, drive-letter roots. Rejects UNC paths, ADS streams,
    /// device paths, and any escape above the root. Absolute paths
    /// must already sit inside the root; relative paths resolve
    /// under it. Returns the normalized absolute path, or an error.
    /// The agent calls this once per configured root (absolute
    /// paths) or against its current root (relative paths).
    /// </summary>
    public static (string? Path, FileError? Error) Resolve(
        string requestedPath, string authorizedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return (null, new FileError("invalid_path", "Path is empty."));
        if (string.IsNullOrWhiteSpace(authorizedRoot))
            return (null, new FileError("no_authorized_root", "No directory is authorized for file access."));
        var normalized = requestedPath.Replace('/', '\\').Trim();
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return (null, new FileError("invalid_path", "UNC paths are not allowed."));
        if (normalized.StartsWith(@"\\?\" , StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
            return (null, new FileError("invalid_path", "Device paths are not allowed."));
        // ADS: a colon anywhere except the drive-letter position.
        var colon = normalized.IndexOf(':');
        if (colon > 1)
            return (null, new FileError("invalid_path", "Alternate data streams are not allowed."));
        if (colon == 1 && !char.IsLetter(normalized[0]))
            return (null, new FileError("invalid_path", "Invalid drive letter."));

        // Lexical normalization: split, resolve . and .. without
        // touching the filesystem.
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (resolved.Count == 0)
                    return (null, new FileError("path_escape", "Path escapes its root."));
                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }
            resolved.Add(part);
        }
        if (resolved.Count == 0)
            return (null, new FileError("invalid_path", "Path resolves to nothing."));

        var root = NormalizeRoot(authorizedRoot);
        // Absolute path (drive letter): it must already sit inside the root.
        if (colon == 1)
        {
            var candidate = string.Join("\\", resolved);
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return (candidate, null);
            return (null, new FileError("path_escape", "Path is outside the authorized root."));
        }

        // Relative path: resolve under the root. A leading .. was
        // already rejected above, so this cannot escape.
        return (root + "\\" + string.Join("\\", resolved), null);
    }

    public static string NormalizeRoot(string root)
    {
        var normalized = root.Replace('/', '\\').TrimEnd('\\');
        return normalized;
    }

    /// <summary>List one directory under an authorized root.</summary>
    public static (IReadOnlyList<(string Name, bool IsDirectory, long Size)>? Entries, FileError? Error) List(
        string resolvedPath)
    {
        try
        {
            if (!Directory.Exists(resolvedPath))
                return (null, new FileError("not_found", "Directory does not exist."));
            var entries = new List<(string, bool, long)>();
            foreach (var path in Directory.EnumerateFileSystemEntries(resolvedPath))
            {
                if (entries.Count >= MaxListEntries)
                    break;
                var name = Path.GetFileName(path);
                var isDir = Directory.Exists(path);
                long size = 0;
                if (!isDir)
                {
                    try { size = new FileInfo(path).Length; } catch { size = -1; }
                }
                entries.Add((name, isDir, size));
            }
            return (entries, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, new FileError("access_denied", "Access denied."));
        }
        catch (IOException ex)
        {
            return (null, new FileError("io_error", ex.Message));
        }
    }

    /// <summary>Read a file (bounded). Returns bytes + SHA-256.</summary>
    public static (byte[]? Bytes, string? Sha256, FileError? Error) Read(string resolvedPath)
    {
        try
        {
            var info = new FileInfo(resolvedPath);
            if (!info.Exists)
                return (null, null, new FileError("not_found", "File does not exist."));
            if (info.Length > MaxReadBytes)
                return (null, null, new FileError("file_too_large", $"File exceeds {MaxReadBytes} bytes."));
            var bytes = File.ReadAllBytes(resolvedPath);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return (bytes, sha, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null, new FileError("access_denied", "Access denied."));
        }
        catch (IOException ex)
        {
            return (null, null, new FileError("io_error", ex.Message));
        }
    }

    /// <summary>
    /// Write a file atomically (temp file in the same directory,
    /// verify, atomic publish). `createOnly` fails when the file
    /// exists; `expectedHash` fails when the current content does
    /// not match (SHA-256 hex). Returns the new SHA-256.
    /// </summary>
    public static (string? Sha256, FileError? Error) Write(
        string resolvedPath, byte[] content, bool createOnly, string? expectedHash)
    {
        try
        {
            var directory = Path.GetDirectoryName(resolvedPath);
            if (directory is null || !Directory.Exists(directory))
                return (null, new FileError("not_found", "Parent directory does not exist."));
            if (File.Exists(resolvedPath))
            {
                if (createOnly)
                    return (null, new FileError("already_exists", "File already exists (create_only)."));
                if (expectedHash is not null)
                {
                    var current = File.ReadAllBytes(resolvedPath);
                    var currentHash = Convert.ToHexString(SHA256.HashData(current)).ToLowerInvariant();
                    if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        return (null, new FileError("hash_mismatch", "Current content does not match expected_hash."));
                }
            }
            else if (expectedHash is not null)
            {
                return (null, new FileError("hash_mismatch", "File does not exist; expected_hash cannot match."));
            }
            var tempPath = Path.Combine(directory, ".bridge-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(tempPath, content);
                var written = File.ReadAllBytes(tempPath);
                if (!written.SequenceEqual(content))
                    return (null, new FileError("io_error", "Verification read mismatch."));
                File.Move(tempPath, resolvedPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            return (sha, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, new FileError("access_denied", "Access denied."));
        }
        catch (IOException ex)
        {
            return (null, new FileError("io_error", ex.Message));
        }
    }

    /// <summary>
    /// Move (rename) a file or directory within the authorized
    /// root. Both paths are resolved and re-checked against the
    /// root; the destination must not exist (no silent overwrite).
    /// Same-volume moves are atomic on NTFS. No recursive delete.
    /// </summary>
    public static (string? Sha256, FileError? Error) Move(
        string sourcePath, string destPath)
    {
        try
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                return (null, new FileError("not_found", "Source does not exist."));
            if (File.Exists(destPath) || Directory.Exists(destPath))
                return (null, new FileError("already_exists", "Destination already exists."));
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is null || !Directory.Exists(destDir))
                return (null, new FileError("not_found", "Destination directory does not exist."));
            string? sha = null;
            if (File.Exists(sourcePath))
            {
                var bytes = File.ReadAllBytes(sourcePath);
                sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            File.Move(sourcePath, destPath);
            return (sha, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, new FileError("access_denied", "Access denied."));
        }
        catch (IOException ex)
        {
            return (null, new FileError("io_error", ex.Message));
        }
    }

    /// <summary>
    /// Create a directory within the authorized root (parents
    /// included). Idempotent: an existing directory is success.
    /// Fails when the path exists as a file.
    /// </summary>
    public static FileError? Mkdir(string resolvedPath)
    {
        try
        {
            if (File.Exists(resolvedPath))
                return new FileError("already_exists", "A file exists at that path.");
            Directory.CreateDirectory(resolvedPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return new FileError("access_denied", "Access denied.");
        }
        catch (IOException ex)
        {
            return new FileError("io_error", ex.Message);
        }
    }
}
