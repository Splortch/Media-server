using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MediaServer.Services;

public record GpuDeviceMetric(
    int Adapter,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes
);

public record GpuSnapshot(string Status, DateTime? SampledAt, GpuDeviceMetric[] Devices);

// Explicit opt-in; fixed vendor command, bounded output/lifetime, no UUIDs or private process data.
public sealed class GpuMetrics(bool enabled, string executable = "nvidia-smi")
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GpuSnapshot cached = new(enabled ? "Not sampled" : "Disabled by operator", null, []);
    private DateTime refreshed = DateTime.MinValue;

    public async Task<GpuSnapshot> ReadAsync(CancellationToken ct)
    {
        if (!enabled)
            return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - refreshed < TimeSpan.FromSeconds(5))
                return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(
                    "--query-gpu=index,utilization.gpu,memory.used,memory.total"
                );
                start.ArgumentList.Add("--format=csv,noheader,nounits");
                using var process = Process.Start(start) ?? throw new IOException();
                using var kill = timeout.Token.Register(() => Kill(process));
                var output = ReadBounded(process.StandardOutput, timeout.Token);
                var errors = process.StandardError.BaseStream.CopyToAsync(
                    Stream.Null,
                    timeout.Token
                );
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                    if (process.ExitCode != 0)
                        throw new IOException();
                    cached = Parse(await output);
                }
                finally
                {
                    Kill(process);
                    // Observe output tasks even on process timeout/failure.
                    try
                    {
                        await Task.WhenAll(output, errors);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                }
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or System.ComponentModel.Win32Exception
                            or OperationCanceledException
                            or ArgumentException
                )
            {
                ct.ThrowIfCancellationRequested();
                cached = new(
                    "Unavailable: NVIDIA tool, driver, device or metrics unsupported",
                    null,
                    []
                );
            }
            refreshed = DateTime.UtcNow;
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    private static async Task<string> ReadBounded(TextReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (result.Length + read > 8192)
                throw new IOException();
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    public static GpuSnapshot Parse(string csv)
    {
        if (csv.Length > 8192)
            throw new IOException();
        var devices = new List<GpuDeviceMetric>();
        foreach (
            var row in csv.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var columns = row.Split(',', StringSplitOptions.TrimEntries);
            if (
                columns.Length != 4
                || !int.TryParse(
                    columns[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var adapter
                )
                || adapter is < 0 or > 31
                || devices.Any(x => x.Adapter == adapter)
            )
                throw new IOException();
            var utilization = Number(columns[1], 100);
            var used = Number(columns[2], 2_097_152);
            var total = Number(columns[3], 2_097_152);
            if (used > total)
                throw new IOException();
            devices.Add(
                new(
                    adapter,
                    utilization,
                    used is null ? null : (long)(used * 1024 * 1024),
                    total is null ? null : (long)(total * 1024 * 1024)
                )
            );
        }
        return new(
            devices.Count == 0
                ? "Unavailable: no NVIDIA devices"
                : "NVIDIA host-wide sample (not per stream)",
            devices.Count == 0 ? null : DateTime.UtcNow,
            devices.ToArray()
        );
    }

    private static double? Number(string value, double maximum)
    {
        if (value == "N/A" || value.StartsWith("[N/A", StringComparison.Ordinal))
            return null;
        if (
            !double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number
            )
            || !double.IsFinite(number)
            || number < 0
            || number > maximum
        )
            throw new IOException();
        return number;
    }
}
