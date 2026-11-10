using System.Text;

namespace HamResan.Core;

public static class SafeFiles
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // Treat both separators as invalid on every platform, rather than accepting a remote path.
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException("A file name, not a path, is required.");
        var result = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormC))
            result.Append(char.IsControl(c) || "<>:\"|?*".Contains(c) ? '_' : c);
        string safe = result.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length == 0 || safe is "." or "..") throw new InvalidDataException("Invalid file name.");
        if (Reserved.Contains(safe.Split('.')[0])) safe = "_" + safe;
        // Leave room for a collision suffix and for UTF-8 encodings on Unix filesystems.
        while (Encoding.UTF8.GetByteCount(safe) > 180) safe = safe[..^1];
        return safe;
    }

    // Temporary file and final file are in the same directory. Move without overwrite is atomic
    // on supported local filesystems and also handles competing receivers.
    public static string Commit(string temporaryPath, string directory, string name)
    {
        string safe = Sanitize(name);
        string stem = Path.GetFileNameWithoutExtension(safe);
        string extension = Path.GetExtension(safe);
        for (int i = 0; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, i == 0 ? safe : $"{stem} ({i}){extension}");
            try { File.Move(temporaryPath, candidate, false); return candidate; }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("Too many files with the same name.");
    }
}
