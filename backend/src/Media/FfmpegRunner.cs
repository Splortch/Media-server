using System.Diagnostics;

namespace MediaServer.Media;

public sealed class FfmpegRunner(string executable)
{
    public async Task RunAsync(
        IEnumerable<string> arguments,
        string directory,
        CancellationToken ct
    )
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = directory,
        };
        foreach (
            var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }.Concat(
                arguments
            )
        )
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("FFmpeg unavailable.");
        using var kill = ct.Register(() =>
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException) { }
        });
        // Drain rather than retain potentially sensitive source paths/output in logs or memory.
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(error, output);
            if (process.ExitCode != 0)
                throw new IOException("FFmpeg conversion failed.");
        }
        finally
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException) { }
            try
            {
                await Task.WhenAll(error, output);
            }
            catch (OperationCanceledException) { }
        }
    }
}

public record TranscodeSettings(string[] AllowedEncoders);

public record EncoderCapability(string Encoder, bool Available, string Diagnostic);

public sealed class EncoderDiscovery(FfmpegRunner runner)
{
    public static readonly string[] Encoders =
    [
        "libx264",
        "h264_nvenc",
        "h264_qsv",
        "h264_vaapi",
        "h264_amf",
    ];
    private readonly SemaphoreSlim gate = new(1, 1);
    private EncoderCapability[]? cached;

    public async Task<EncoderCapability[]> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null)
                return cached;
            var result = new List<EncoderCapability>();
            foreach (var encoder in Encoders)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var args = new List<string>();
                if (encoder == "h264_vaapi")
                    args.AddRange(["-vaapi_device", "/dev/dri/renderD128"]);
                args.AddRange([
                    "-f",
                    "lavfi",
                    "-i",
                    "color=size=64x64:rate=24",
                    "-t",
                    "0.1",
                    "-threads",
                    "1",
                ]);
                if (encoder == "h264_vaapi")
                    args.AddRange(["-vf", "format=nv12,hwupload"]);
                args.AddRange(TranscodeProfile.EncoderArguments(encoder));
                args.AddRange(["-f", "null", "-"]);
                try
                {
                    await runner.RunAsync(args, Path.GetTempPath(), timeout.Token);
                    result.Add(
                        new(
                            encoder,
                            true,
                            "Synthetic H.264 encode passed; source-specific fallback still applies."
                        )
                    );
                }
                catch (Exception ex)
                    when (ex
                            is IOException
                                or System.ComponentModel.Win32Exception
                                or OperationCanceledException
                    )
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(
                        new(
                            encoder,
                            false,
                            "Encoder, device or driver unavailable. Software fallback will be used."
                        )
                    );
                }
            }
            return cached = result.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }
}
