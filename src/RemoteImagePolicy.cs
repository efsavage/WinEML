namespace WinEML;

/// <summary>
/// The user's persistent opt-in list of domains allowed to serve images into
/// rendered mail. Everything remote stays blocked unless a host matches here
/// (exact or subdomain), and even then only over HTTPS and only for image
/// requests. Stored as a plain text file, one domain per line, in
/// %LOCALAPPDATA%\WinEML — readable, editable, and deletable by the user.
/// </summary>
internal sealed class RemoteImagePolicy
{
    private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinEML", "allowed-image-domains.txt");

    public RemoteImagePolicy()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (Uri.CheckHostName(line) != UriHostNameType.Unknown)
                    _domains.Add(line);
            }
        }
        catch { /* unreadable list = empty list (fail closed) */ }
    }

    public int Count => _domains.Count;

    /// <summary>An allowed domain also covers its subdomains.</summary>
    public bool IsAllowed(string host)
    {
        if (_domains.Contains(host)) return true;
        foreach (string d in _domains)
            if (host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public void Allow(string domain)
    {
        if (Uri.CheckHostName(domain) == UriHostNameType.Unknown) return;
        if (_domains.Add(domain)) Save();
    }

    public void Revoke(string domain)
    {
        // Remove the exact entry, or whichever entry covers this host.
        if (!_domains.Remove(domain))
        {
            string? covering = _domains.FirstOrDefault(d =>
                domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
            if (covering is null || !_domains.Remove(covering)) return;
        }
        Save();
    }

    public void Clear()
    {
        if (_domains.Count == 0) return;
        _domains.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, new[]
            {
                "# Domains allowed to load images in WinEML (HTTPS only, images only).",
                "# One domain per line; subdomains are included automatically.",
            }.Concat(_domains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)));
        }
        catch { /* best effort — worst case the choice doesn't persist */ }
    }
}
