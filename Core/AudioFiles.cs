namespace NHL27Kit.Core;

/// <summary>Reads an audio file into 16-bit interleaved PCM: WAV directly, anything else (MP3, FLAC, OGG, M4A…) through
/// ffmpeg when it is on the PATH or installed by winget.</summary>
public static class AudioFiles
{
    public static readonly string[] Extensions = { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wma" };

    public static (short[] Pcm, int Channels, int Rate) Read(string file)
    {
        if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return WavReader.Read(File.ReadAllBytes(file));
        string ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")).FirstOrDefault(File.Exists);
        if (ffmpeg == null)
        {
            string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            ffmpeg = Directory.Exists(winget) ? Directory.GetDirectories(winget, "*FFmpeg*").SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)).FirstOrDefault() : null;
        }
        if (ffmpeg == null) throw new NotSupportedException($"{Path.GetFileName(file)}: only WAV files can be read without ffmpeg.");
        string tmp = Path.Combine(Path.GetTempPath(), "NHL27Kit", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-y", "-v", "error", "-i", file, "-ar", "48000", "-c:a", "pcm_s16le", tmp }) psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!) { string err = p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new InvalidDataException("ffmpeg: " + err); }
        try { return WavReader.Read(File.ReadAllBytes(tmp)); } finally { File.Delete(tmp); }
    }
}
