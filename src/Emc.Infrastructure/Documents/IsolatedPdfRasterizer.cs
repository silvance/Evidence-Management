using System.Diagnostics;
using System.Text.Json;
using Emc.Application.Documents;
using Microsoft.Extensions.Options;

namespace Emc.Infrastructure.Documents;

/// <summary>
/// PDF rasterization in a KILLABLE CHILD PROCESS (DOC-014). PDFium parses hostile bytes; here it
/// does so in a separate process started per invocation, so that a crash, a hang or a runaway
/// allocation ends with a dead child and a failure category, never a dead worker - and never,
/// since the worker is the only host, a dead IIS.
///
/// The child is this worker's own executable in "render" mode (<c>Emc.OcrWorker render ...</c>);
/// it takes an argument list (never a shell command line), reads one input file and writes one
/// output file inside a private per-invocation folder, and is killed as a process tree when the
/// caller's token or the hard per-invocation timeout fires. Its stdout and stderr are consumed
/// and discarded: a parser's error text can quote the document. Its exit code is a category.
///
/// With no <see cref="SourceDocumentOptions.RenderHelperPath"/> configured the class renders
/// in-process through <see cref="PdfiumRasterizer"/>. That is for tests and for diagnosis on a
/// developer machine only; the deployed worker configures the helper and the render processor
/// refuses to start without it (see Emc.OcrWorker).
/// </summary>
public sealed class IsolatedPdfRasterizer : IPdfRasterizer
{
    public const int ExitOk = 0;
    public const int ExitUnexpected = 1;
    public const int ExitMalformed = 2;
    public const int ExitUsage = 3;

    private static readonly string[] EnvironmentPassThrough =
    [
        "PATH", "HOME", "TEMP", "TMP", "TMPDIR", "SystemRoot", "SYSTEMROOT", "windir", "USERPROFILE",
        "LOCALAPPDATA", "APPDATA", "ProgramData", "DOTNET_ROOT", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "LD_LIBRARY_PATH", "FONTCONFIG_PATH"
    ];

    private readonly SourceDocumentOptions _options;
    private readonly PdfiumRasterizer _inProcess = new();
    private readonly object _infoLock = new();
    private (byte[] Pdf, HelperInfo Info)? _lastInfo;

    public IsolatedPdfRasterizer(IOptions<SourceDocumentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public bool IsIsolated => !string.IsNullOrWhiteSpace(_options.RenderHelperPath);

    public string RendererVersion => IsIsolated ? $"{_inProcess.RendererVersion} (isolated process)" : _inProcess.RendererVersion;

    public int GetPageCount(byte[] pdf)
        => IsIsolated ? Info(pdf, CancellationToken.None).PageCount : _inProcess.GetPageCount(pdf);

    public IReadOnlyList<PdfPageDimensions> GetPageDimensions(byte[] pdf)
        => IsIsolated
            ? Info(pdf, CancellationToken.None).Pages!.Select(p => new PdfPageDimensions(p.PageNumber, p.WidthPoints, p.HeightPoints)).ToList()
            : _inProcess.GetPageDimensions(pdf);

    public RenderedPage Render(byte[] pdf, int pageNumber, int dpi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!IsIsolated)
        {
            return _inProcess.Render(pdf, pageNumber, dpi, ct);
        }

        var png = RunHelper(pdf, ["page", "--page", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), "--dpi", dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)], "page.png", ct);
        var (width, height) = ReadPngDimensions(png);
        return new RenderedPage(pageNumber, width, height, dpi, png);
    }

    private HelperInfo Info(byte[] pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        lock (_infoLock)
        {
            if (_lastInfo is { } cached && ReferenceEquals(cached.Pdf, pdf))
            {
                return cached.Info;
            }
        }

        var json = RunHelper(pdf, ["info"], "info.json", ct);
        HelperInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<HelperInfo>(json);
        }
        catch (JsonException ex)
        {
            throw new RendererCrashedException("The render helper produced an unreadable manifest.", ex);
        }

        if (info is null || info.PageCount < 0 || info.Pages is null || info.Pages.Count != info.PageCount
            || info.Pages.Select((p, i) => p.PageNumber == i + 1).Any(ok => !ok))
        {
            throw new RendererCrashedException("The render helper produced an inconsistent manifest.");
        }

        lock (_infoLock)
        {
            _lastInfo = (pdf, info);
        }

        return info;
    }

    /// <summary>Starts the helper on a private copy of the bytes, waits under the timeout, kills on expiry, returns the output file's bytes.</summary>
    private byte[] RunHelper(byte[] pdf, IReadOnlyList<string> modeArguments, string outputName, CancellationToken ct)
    {
        var helper = _options.RenderHelperPath;
        var workRoot = string.IsNullOrWhiteSpace(_options.RenderWorkRoot) ? Path.Combine(Path.GetTempPath(), "emc-render") : _options.RenderWorkRoot;
        var workDir = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var input = Path.Combine(workDir, "input.pdf");
            File.WriteAllBytes(input, pdf);
            var output = Path.Combine(workDir, outputName);

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workDir
            };

            if (helper.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = ResolveDotnetHost();
                psi.ArgumentList.Add(helper);
            }
            else
            {
                psi.FileName = helper;
            }

            psi.ArgumentList.Add("render");
            foreach (var a in modeArguments)
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("--input");
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(output);

            // A minimal, explicit environment: what the runtime needs to start and nothing that
            // says anything about this deployment.
            psi.Environment.Clear();
            foreach (var name in EnvironmentPassThrough)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    psi.Environment[name] = value;
                }
            }

            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

            using var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start())
                {
                    throw new RendererCrashedException("The render helper did not start.");
                }
            }
            catch (Exception ex) when (ex is not RendererCrashedException)
            {
                throw new RendererCrashedException("The render helper could not be started.", ex);
            }

            // Consumed, never logged, never surfaced.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            var deadline = Stopwatch.StartNew();
            var hardLimit = TimeSpan.FromSeconds(Math.Max(1, _options.RenderTimeoutSeconds));
            var timedOut = false;
            while (!process.WaitForExit(200))
            {
                if (ct.IsCancellationRequested || deadline.Elapsed > hardLimit)
                {
                    timedOut = true;
                    KillTree(process);
                    break;
                }
            }

            if (timedOut)
            {
                // A killed child is a timeout, whichever clock fired: the caller's or the hard limit.
                throw new OperationCanceledException("The render helper exceeded its time limit and was killed.", ct.IsCancellationRequested ? ct : CancellationToken.None);
            }

            process.WaitForExit();
            try { stdout.Wait(TimeSpan.FromSeconds(2)); stderr.Wait(TimeSpan.FromSeconds(2)); } catch { /* discarded */ }

            switch (process.ExitCode)
            {
                case ExitOk:
                    break;
                case ExitMalformed:
                    throw new MalformedPdfException("The render helper could not open the document.");
                default:
                    throw new RendererCrashedException($"The render helper ended with exit code {process.ExitCode}.");
            }

            if (!File.Exists(output))
            {
                throw new RendererCrashedException("The render helper produced no output.");
            }

            var bytes = File.ReadAllBytes(output);
            if (bytes.Length == 0)
            {
                throw new RendererCrashedException("The render helper produced an empty output.");
            }

            return bytes;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }

        try { process.WaitForExit(5000); } catch { /* ignore */ }
    }

    /// <summary>The dotnet muxer that runs this process, or DOTNET_ROOT's, or the one on PATH.</summary>
    internal static string ResolveDotnetHost()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileNameWithoutExtension(current);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    /// <summary>Width and height from the PNG header (IHDR), so the parent trusts the bytes it holds, not a claim.</summary>
    internal static (int Width, int Height) ReadPngDimensions(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(signature)
            || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            throw new RendererCrashedException("The render helper's output is not a PNG.");
        }

        var width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
        {
            throw new RendererCrashedException("The render helper's output has no size.");
        }

        return (width, height);
    }

    /// <summary>The helper's manifest for "info" mode. Written by Emc.OcrWorker's render mode; read here.</summary>
    public sealed class HelperInfo
    {
        public int PageCount { get; set; }
        public List<HelperPage>? Pages { get; set; }
    }

    public sealed class HelperPage
    {
        public int PageNumber { get; set; }
        public double WidthPoints { get; set; }
        public double HeightPoints { get; set; }
    }
}
