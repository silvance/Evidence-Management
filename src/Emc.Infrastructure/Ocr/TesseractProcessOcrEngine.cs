using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Emc.Application.Ocr;
using Emc.Domain.Ocr;
using Microsoft.Extensions.Options;

namespace Emc.Infrastructure.Ocr;

/// <summary>
/// Tesseract 5 as an EXTERNAL PROCESS (docs/ocr-engine-evaluation.md).
///
/// Controls (Phase 10):
///   - the executable path and tessdata folder come from configuration, never from PATH lookup
///     or an environment the engine chooses;
///   - arguments are an argument LIST (no shell, no string interpolation into a command line);
///   - every invocation runs in a fresh private directory under the configured work root, named
///     by a random GUID, and that directory is deleted in a finally block;
///   - a hard timeout kills the whole process tree;
///   - the child's environment is minimal: TESSDATA_PREFIX set explicitly, OMP_THREAD_LIMIT=1
///     so one job cannot take every core, and nothing inherited beyond what the OS needs;
///   - stdout/stderr are consumed and discarded except for the fixed-format OSD lines the
///     engine is asked for; nothing the engine prints is logged or thrown;
///   - the engine's own network use: none. Tesseract has no network code.
///
/// At construction each installed artifact - the engine binary and every model file - is hashed
/// and compared with the APPROVED hash from configuration (OCR-017): a mismatch or a missing
/// entry is an explicit start-up failure with its own category, before the binary is ever
/// executed. Then the binary is executed with --version, under a timeout that kills it, so a
/// missing or hung engine is a start-up failure (Phase 12), not a queue that never drains.
/// </summary>
public sealed class TesseractProcessOcrEngine : IOcrEngine
{
    private readonly OcrOptions _options;
    private readonly string _workRoot;

    public TesseractProcessOcrEngine(IOptions<OcrOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.EnginePath) || !File.Exists(_options.EnginePath))
        {
            throw new OcrEngineException(OcrFailureCategory.EngineUnavailable);
        }

        if (string.IsNullOrWhiteSpace(_options.TessdataPath) || !Directory.Exists(_options.TessdataPath))
        {
            throw new OcrEngineException(OcrFailureCategory.ModelMissing);
        }

        if (string.IsNullOrWhiteSpace(_options.WorkRoot))
        {
            throw new InvalidOperationException("Ocr:WorkRoot is not configured.");
        }

        _workRoot = Path.GetFullPath(_options.WorkRoot);
        Directory.CreateDirectory(_workRoot);

        var approved = _options.ApprovedArtifactHashes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var verifying = approved.Count > 0;
        if (_options.RequireApprovedArtifactHashes && !verifying)
        {
            // An engine nobody approved is not an engine this worker runs.
            throw new OcrEngineException(OcrFailureCategory.ArtifactNotApproved);
        }

        if (verifying)
        {
            RequireApproved(approved, _options.EnginePath);
        }

        var models = new List<OcrModelInfo>();
        foreach (var language in _options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append("osd").Distinct(StringComparer.Ordinal))
        {
            var path = Path.Combine(_options.TessdataPath, $"{language}.traineddata");
            if (!File.Exists(path))
            {
                throw new OcrEngineException(OcrFailureCategory.ModelMissing);
            }

            var hash = verifying ? RequireApproved(approved, path) : HashFile(path);
            models.Add(new OcrModelInfo(language, hash));
        }

        Models = models;
        ArtifactsVerifiedAgainstApprovedHashes = verifying;
        EngineVersion = ReadVersion();
    }

    /// <summary>True when every installed artifact was checked against the approved list at start (OCR-017).</summary>
    public bool ArtifactsVerifiedAgainstApprovedHashes { get; }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The file's hash, if and only if it is the approved one for that file name.</summary>
    private static string RequireApproved(IReadOnlyDictionary<string, string> approved, string path)
    {
        var name = Path.GetFileName(path);
        if (!approved.TryGetValue(name, out var expected) || string.IsNullOrWhiteSpace(expected))
        {
            throw new OcrEngineException(OcrFailureCategory.ArtifactNotApproved);
        }

        var actual = HashFile(path);
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new OcrEngineException(OcrFailureCategory.ArtifactNotApproved);
        }

        return actual;
    }

    public string EngineName => "tesseract";
    public string EngineVersion { get; }
    public IReadOnlyList<OcrModelInfo> Models { get; }

    public async Task<OrientationResult> DetectOrientationAsync(byte[] png, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        var (exitCode, stdout) = await RunAsync(png, dir => ["input.png", "-", "--tessdata-dir", _options.TessdataPath, "--psm", "0"], ct);
        if (exitCode != 0)
        {
            // OSD fails on pages with too little text. That is not an engine failure; it is
            // "no opinion", and the caller falls back to trying orientations.
            return new OrientationResult(0, 0m);
        }

        var rotate = 0;
        var confidence = 0m;
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Rotate:", StringComparison.Ordinal) && int.TryParse(trimmed["Rotate:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            {
                rotate = ((r % 360) + 360) % 360;
            }
            else if (trimmed.StartsWith("Orientation confidence:", StringComparison.Ordinal) && decimal.TryParse(trimmed["Orientation confidence:".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            {
                confidence = c;
            }
        }

        return new OrientationResult(rotate, confidence);
    }

    /// <summary>
    /// Two passes, merged. No single page-segmentation mode reads a boxed form reliably: psm 3
    /// (automatic layout) keeps the small printed labels inside boxed rows but, on some inputs,
    /// drops the value line beneath a label; psm 6 (one uniform block) keeps every value line
    /// but drops the small labels. The union - psm 3 words, plus any psm 6 word that lands where
    /// psm 3 read nothing - covers both. The cost is a second engine pass per page, in a worker.
    /// </summary>
    public async Task<OcrPageResult> RecognizeAsync(byte[] png, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        var primary = await RecognizeWithAsync(png, "3", ct);
        var secondary = await RecognizeWithAsync(png, "6", ct);
        return MergeWords(primary, secondary);
    }

    private async Task<OcrPageResult> RecognizeWithAsync(byte[] png, string psm, CancellationToken ct)
    {
        var (exitCode, stdout) = await RunAsync(png,
            dir => ["input.png", "-", "--tessdata-dir", _options.TessdataPath, "-l", _options.Languages, "--psm", psm, "tsv"], ct);
        if (exitCode != 0)
        {
            throw new OcrEngineException(OcrFailureCategory.EngineCrashed);
        }

        return ParseTsv(stdout);
    }

    /// <summary>
    /// Keeps every primary word; adds a secondary word only where it overlaps no primary word
    /// by more than a third of its own area. Added words keep their own line grouping, offset
    /// so they never merge into a primary line.
    /// </summary>
    internal static OcrPageResult MergeWords(OcrPageResult primary, OcrPageResult secondary)
    {
        const int blockOffset = 100_000;
        var words = new List<OcrWord>(primary.Words);
        foreach (var w in secondary.Words)
        {
            var area = Math.Max(1, w.Width * w.Height);
            var covered = primary.Words.Any(p =>
            {
                var ix = Math.Max(0, Math.Min(p.Left + p.Width, w.Left + w.Width) - Math.Max(p.Left, w.Left));
                var iy = Math.Max(0, Math.Min(p.Top + p.Height, w.Top + w.Height) - Math.Max(p.Top, w.Top));
                return (double)(ix * iy) / area > 0.33;
            });
            if (!covered)
            {
                words.Add(w with { BlockIndex = w.BlockIndex + blockOffset });
            }
        }

        return new OcrPageResult(words, Math.Max(primary.ImageWidth, secondary.ImageWidth), Math.Max(primary.ImageHeight, secondary.ImageHeight));
    }

    /// <summary>
    /// Tesseract's tsv: level page block par line word left top width height conf text. Level 5
    /// rows are words; conf is -1 on structural rows. Only words are kept.
    /// </summary>
    internal static OcrPageResult ParseTsv(string tsv)
    {
        var words = new List<OcrWord>();
        var width = 0;
        var height = 0;
        foreach (var raw in tsv.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var cols = line.Split('\t');
            if (cols.Length < 12 || !int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                continue;
            }

            var left = Int(cols[6]); var top = Int(cols[7]); var w = Int(cols[8]); var h = Int(cols[9]);
            if (level == 1)
            {
                width = w; height = h;
                continue;
            }

            if (level != 5)
            {
                continue;
            }

            if (!decimal.TryParse(cols[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) || conf < 0)
            {
                continue;
            }

            var text = string.Join('\t', cols.Skip(11)).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            words.Add(new OcrWord(text, Math.Clamp(Math.Round(conf, 2), 0m, 100m), left, top, w, h, Int(cols[2]), Int(cols[3]), Int(cols[4]), Int(cols[5])));
        }

        return new OcrPageResult(words, width, height);

        static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Time allowed for the binary to print its version. A binary that hangs here is killed and reported unavailable, never waited on.</summary>
    internal static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    private string ReadVersion()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = StartInfo(_workRoot);
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();

            // Output is drained asynchronously so a chatty or silent binary cannot deadlock the
            // probe, and the wait is bounded: on expiry the process TREE is killed.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)VersionProbeTimeout.TotalMilliseconds))
            {
                TryKill(process);
                throw new OcrEngineException(OcrFailureCategory.EngineUnavailable);
            }

            process.WaitForExit();
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)))
            {
                throw new OcrEngineException(OcrFailureCategory.EngineUnavailable);
            }

            var text = stdout.Result + stderr.Result;
            var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("tesseract", StringComparison.OrdinalIgnoreCase));
            if (process.ExitCode != 0 || first is null)
            {
                throw new OcrEngineException(OcrFailureCategory.EngineUnavailable);
            }

            return first["tesseract".Length..].Trim();
        }
        catch (Exception ex) when (ex is not OcrEngineException)
        {
            throw new OcrEngineException(OcrFailureCategory.EngineUnavailable, ex);
        }
    }

    private async Task<(int ExitCode, string Stdout)> RunAsync(byte[] png, Func<string, string[]> arguments, CancellationToken ct)
    {
        var dir = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "input.png"), png, ct);

            using var process = new Process();
            process.StartInfo = StartInfo(dir);
            foreach (var a in arguments(dir))
            {
                process.StartInfo.ArgumentList.Add(a);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask;
            _ = await stderrTask; // consumed, never surfaced
            return (process.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OcrEngineException)
        {
            throw new OcrEngineException(OcrFailureCategory.EngineCrashed, ex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private ProcessStartInfo StartInfo(string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = _options.EnginePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        // A minimal, explicit environment. Nothing the worker inherited is passed on except what
        // the OS loader needs to find the engine's own libraries.
        var inherited = info.Environment.ToList();
        info.Environment.Clear();
        foreach (var keep in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "LD_LIBRARY_PATH", "HOME" })
        {
            var value = inherited.FirstOrDefault(e => string.Equals(e.Key, keep, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null)
            {
                info.Environment[keep] = value;
            }
        }

        info.Environment["TESSDATA_PREFIX"] = _options.TessdataPath;
        info.Environment["OMP_THREAD_LIMIT"] = "1";
        return info;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
