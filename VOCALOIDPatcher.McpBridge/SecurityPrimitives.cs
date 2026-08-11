namespace VOCALOIDPatcher.McpBridge;

public sealed class WriteLeaseManager
{
    private readonly object _gate = new();
    private readonly TimeSpan _idleTimeout;
    private string? _clientId;
    private string? _clientName;
    private DateTimeOffset _lastActivityUtc;
    private int _activeJobs;

    public WriteLeaseManager(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
    }

    public object Snapshot(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            ExpireIfIdle(nowUtc);
            return new
            {
                held = _clientId != null,
                client_id = _clientId,
                client_name = _clientName,
                expires_at_utc = _clientId == null || _activeJobs > 0
                    ? (DateTimeOffset?)null
                    : _lastActivityUtc + _idleTimeout,
                active_jobs = _activeJobs,
            };
        }
    }

    public bool TryAcquire(string clientId, string clientName, DateTimeOffset nowUtc, out string? heldBy)
    {
        lock (_gate)
        {
            ExpireIfIdle(nowUtc);
            if (_clientId != null && !string.Equals(_clientId, clientId, StringComparison.Ordinal))
            {
                heldBy = _clientName;
                return false;
            }

            _clientId = clientId;
            _clientName = clientName;
            _lastActivityUtc = nowUtc;
            heldBy = null;
            return true;
        }
    }

    public bool Touch(string clientId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            ExpireIfIdle(nowUtc);
            if (!string.Equals(_clientId, clientId, StringComparison.Ordinal))
                return false;
            _lastActivityUtc = nowUtc;
            return true;
        }
    }

    public bool Release(string clientId)
    {
        lock (_gate)
        {
            if (!string.Equals(_clientId, clientId, StringComparison.Ordinal))
                return false;
            Clear();
            return true;
        }
    }

    public void Revoke()
    {
        lock (_gate)
            Clear();
    }

    public bool BeginJob(string clientId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            ExpireIfIdle(nowUtc);
            if (!string.Equals(_clientId, clientId, StringComparison.Ordinal))
                return false;
            _lastActivityUtc = nowUtc;
            _activeJobs++;
            return true;
        }
    }

    public void EndJob(string clientId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!string.Equals(_clientId, clientId, StringComparison.Ordinal))
                return;
            _activeJobs = Math.Max(0, _activeJobs - 1);
            _lastActivityUtc = nowUtc;
        }
    }

    private void ExpireIfIdle(DateTimeOffset nowUtc)
    {
        if (_clientId != null && _activeJobs == 0 && nowUtc - _lastActivityUtc >= _idleTimeout)
            Clear();
    }

    private void Clear()
    {
        _clientId = null;
        _clientName = null;
        _lastActivityUtc = default;
        _activeJobs = 0;
    }
}

public sealed class PathAllowlist
{
    private readonly string[] _roots;

    public PathAllowlist(IEnumerable<string> roots)
    {
        var normalized = new List<string>();
        foreach (string path in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                normalized.Add(NormalizeDirectory(path));
            }
            catch
            {
                // Invalid configured roots grant no access.
            }
        }
        _roots = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryResolve(string path, out string fullPath, out string? reason)
    {
        fullPath = string.Empty;
        reason = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "A file path is required.";
            return false;
        }

        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            reason = "UNC and device paths are not allowed.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The path is invalid.";
            return false;
        }

        int colon = fullPath.IndexOf(':', 2);
        if (colon >= 0)
        {
            reason = "Alternate data streams are not allowed.";
            return false;
        }

        foreach (string root in _roots)
        {
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(root, fullPath);
                if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && relative != "..")
                {
                    try
                    {
                        string canonicalRoot = Canonicalize(root);
                        string canonicalTarget = Canonicalize(fullPath);
                        if (canonicalTarget.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                            return true;
                        reason = "The path escapes the allowlist through a symbolic link or junction.";
                        return false;
                    }
                    catch
                    {
                        reason = "The path could not be safely resolved.";
                        return false;
                    }
                }
            }
        }

        reason = "The path is outside the current project directory and configured allowlist.";
        return false;
    }

    private static string NormalizeDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
    }

    private static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);
        var missing = new Stack<string>();
        string? probe = Path.TrimEndingDirectorySeparator(full);
        while (probe != null && !File.Exists(probe) && !Directory.Exists(probe))
        {
            string? name = Path.GetFileName(probe);
            if (!string.IsNullOrEmpty(name))
                missing.Push(name);
            probe = Path.GetDirectoryName(probe);
        }

        if (probe == null)
            throw new IOException("No existing ancestor could be resolved.");
        FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);
        FileSystemInfo? target = info.ResolveLinkTarget(true);
        string resolved = target?.FullName ?? info.FullName;
        while (missing.TryPop(out string? segment))
            resolved = Path.Combine(resolved, segment);
        resolved = Path.GetFullPath(resolved);
        return Directory.Exists(full) || full.EndsWith(Path.DirectorySeparatorChar)
            ? NormalizeDirectory(resolved)
            : resolved;
    }
}
