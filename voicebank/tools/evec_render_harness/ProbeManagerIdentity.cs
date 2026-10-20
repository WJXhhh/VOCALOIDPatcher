using System;

internal static class ProbeManagerIdentity
{
    // VSM removes the entire application's temporary root at manager startup
    // and disposal. Sharing an ID with a live editor or another probe destroys
    // that process's sequence directories, even without editing its project.
    internal static string Create(string? label, int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        string prefix = "VOCALOIDPatcher.Probe." + processId;
        if (string.IsNullOrWhiteSpace(label)) return prefix;
        if (label.Length > 80 || Array.Exists(label.ToCharArray(), c =>
            !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Probe manager label must contain at most 80 ASCII letters, digits, dots, underscores or hyphens.", nameof(label));
        return prefix + "." + label;
    }
}
