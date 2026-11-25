using System.Text.Json;
using Bridge.DesktopCore;
using Bridge.WindowsAgent.Contracts;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// File backend for the Windows agent (T22, docs/04 §11).
/// The authorized roots come from the local config file, which
/// only the local user edits (tray UI / config). Default: no
/// roots — file access is closed until the user authorizes a
/// directory. Symlinks and reparse points are resolved to their
/// final target BEFORE the lexical root check, so a link inside
/// the root that points outside it is rejected.
/// </summary>
public sealed class FileBackend
{
    private volatile IReadOnlyList<string> _roots;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public FileBackend(IReadOnlyList<string> authorizedRoots)
    {
        _roots = authorizedRoots;
    }

    public IReadOnlyList<string> AuthorizedRoots => _roots;

    /// <summary>Replace the authorized roots (tray UI, local user only).</summary>
    public void SetRoots(IReadOnlyList<string> authorizedRoots) => _roots = authorizedRoots;

    /// <summary>Resolve + reparse-check a requested path.</summary>
    public (string? Path, RestrictedFileAccess.FileError? Error) ResolvePath(string requestedPath)
    {
        if (_roots.Count == 0)
            return (null, new RestrictedFileAccess.FileError(
                "no_authorized_root", "No directory is authorized for file access."));
        // Relative paths resolve under the first root; absolute
        // paths are matched against every root.
        foreach (var root in _roots)
        {
            var (path, error) = RestrictedFileAccess.Resolve(requestedPath, root);
            if (error is not null)
            {
                // An absolute path that fails one root may match
                // another; a relative path only tries the first root.
                if (requestedPath.Contains(':') || requestedPath.StartsWith(@"\\"))
                    continue;
                return (null, error);
            }
            var final = ResolveLinks(path!);
            if (final is null)
                return (null, new RestrictedFileAccess.FileError(
                    "io_error", "Could not resolve the final path."));
            var (checkedPath, checkError) = RestrictedFileAccess.Resolve(final, root);
            if (checkError is not null)
                return (null, checkError);
            return (checkedPath, null);
        }
        return (null, new RestrictedFileAccess.FileError(
            "path_escape", "Path is outside every authorized root."));
    }

    private static string? ResolveLinks(string path)
    {
        try
        {
            // Walk up until an existing ancestor, resolve it, then
            // re-append the non-existing tail.
            var existing = path;
            var tail = new Stack<string>();
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null || parent == existing)
                    return path;
                tail.Push(Path.GetFileName(existing));
                existing = parent;
            }
            var resolved = File.ResolveLinkTarget(existing, returnFinalTarget: true)?.FullName ?? existing;
            while (tail.Count > 0)
                resolved = Path.Combine(resolved, tail.Pop());
            return resolved;
        }
        catch
        {
            return null;
        }
    }

    public object List(string requestedPath)
    {
        var (path, error) = ResolvePath(requestedPath);
        if (error is not null)
            return new { status = "failed", error = error.Code, error_message = error.Message };
        var (entries, listError) = RestrictedFileAccess.List(path!);
        if (listError is not null)
            return new { status = "failed", error = listError.Code, error_message = listError.Message };
        return new
        {
            status = "succeeded",
            path,
            entries = entries!.Select(e => new
            {
                name = e.Name,
                is_directory = e.IsDirectory,
                size = e.Size,
            }).ToList(),
        };
    }

    public object Read(string requestedPath)
    {
        var (path, error) = ResolvePath(requestedPath);
        if (error is not null)
            return new { status = "failed", error = error.Code, error_message = error.Message };
        var (bytes, sha, readError) = RestrictedFileAccess.Read(path!);
        if (readError is not null)
            return new { status = "failed", error = readError.Code, error_message = readError.Message };
        return new
        {
            status = "succeeded",
            path,
            sha256 = sha,
            byte_length = bytes!.Length,
            content_b64 = Convert.ToBase64String(bytes),
        };
    }

    public object Write(FileWritePayload payload)
    {
        var (path, error) = ResolvePath(payload.Path);
        if (error is not null)
            return new { status = "failed", error = error.Code, error_message = error.Message };
        byte[] content;
        try
        {
            content = Convert.FromBase64String(payload.ContentB64);
        }
        catch (FormatException)
        {
            return new { status = "failed", error = "invalid_payload", error_message = "content_b64 is not valid base64." };
        }
        if (content.Length > RestrictedFileAccess.MaxReadBytes)
            return new { status = "failed", error = "file_too_large", error_message = $"Content exceeds {RestrictedFileAccess.MaxReadBytes} bytes." };
        var (sha, writeError) = RestrictedFileAccess.Write(
            path!, content, payload.CreateOnly, payload.ExpectedHash);
        if (writeError is not null)
            return new { status = "failed", error = writeError.Code, error_message = writeError.Message };
        return new { status = "succeeded", path, sha256 = sha, byte_length = content.Length };
    }

    public object Mkdir(string requestedPath)
    {
        var (path, error) = ResolvePath(requestedPath);
        if (error is not null)
            return new { status = "failed", error = error.Code, error_message = error.Message };
        var mkdirError = RestrictedFileAccess.Mkdir(path!);
        if (mkdirError is not null)
            return new { status = "failed", error = mkdirError.Code, error_message = mkdirError.Message };
        return new { status = "succeeded", path };
    }

    public object Move(FileMovePayload payload)
    {
        var (source, sourceError) = ResolvePath(payload.SourcePath);
        if (sourceError is not null)
            return new { status = "failed", error = sourceError.Code, error_message = sourceError.Message };
        var (dest, destError) = ResolvePath(payload.DestPath);
        if (destError is not null)
            return new { status = "failed", error = destError.Code, error_message = destError.Message };
        var (sha, moveError) = RestrictedFileAccess.Move(source!, dest!);
        if (moveError is not null)
            return new { status = "failed", error = moveError.Code, error_message = moveError.Message };
        return new { status = "succeeded", source_path = source, dest_path = dest, sha256 = sha };
    }

    /// <summary>Dispatch one file command payload (from the channel loop).</summary>
    public object Dispatch(string operation, JsonElement payload)
    {
        try
        {
            return operation switch
            {
                OperationNames.FileList => List(
                    JsonSerializer.Deserialize<FileListPayload>(payload.GetRawText(), _json)!.Path),
                OperationNames.FileRead => Read(
                    JsonSerializer.Deserialize<FileReadPayload>(payload.GetRawText(), _json)!.Path),
                OperationNames.FileWrite => Write(
                    JsonSerializer.Deserialize<FileWritePayload>(payload.GetRawText(), _json)!),
                OperationNames.FileMove => Move(
                    JsonSerializer.Deserialize<FileMovePayload>(payload.GetRawText(), _json)!),
                OperationNames.FileMkdir => Mkdir(
                    JsonSerializer.Deserialize<FileMkdirPayload>(payload.GetRawText(), _json)!.Path),
                _ => new { status = "failed", error = "unsupported_operation", error_message = $"Operation '{operation}' is not a file operation." },
            };
        }
        catch (JsonException ex)
        {
            return new { status = "failed", error = "invalid_payload", error_message = ex.Message };
        }
    }
}
