using Shared;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mirage;

/// <summary>
/// Пересборка fMP4-сегмента (init + .m4s) в MPEG-TS без перекодирования: ffmpeg -c copy.
/// </summary>
public static class MirageRemux
{
    static readonly ConcurrentDictionary<string, bool> checkedPaths = new();
    static readonly SemaphoreSlim jobs = new(4, 4);

    // Serilog в Lampac пишет только Error в файл, поэтому дублируем в консоль (docker logs)
    static void Report(string msg)
    {
        Console.WriteLine(msg);
        Serilog.Log.Error(msg);
    }

    public static string ResolveFfmpeg(string conf)
    {
        if (!string.IsNullOrWhiteSpace(conf))
            return conf;

        if (CoreInit.Win32NT)
            return "data/ffmpeg.exe";

        return File.Exists("data/ffmpeg") ? "data/ffmpeg" : "ffmpeg";
    }

    async public static Task<bool> IsAvailable(string conf)
    {
        string path = ResolveFfmpeg(conf);

        if (checkedPaths.TryGetValue(path, out bool ok))
            return ok;

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");

            using (var p = Process.Start(psi))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                _ = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();

                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                ok = p.ExitCode == 0;
            }
        }
        catch
        {
            ok = false;
        }

        if (!ok)
            Report($"Mirage: ffmpeg не найден ({path}), ремакс fMP4->TS отключён. Укажите Mirage.ffmpeg в init.conf");

        checkedPaths[path] = ok;
        return ok;
    }

    async public static Task<byte[]> Remux(string ffmpeg, byte[] init, byte[] seg, CancellationToken ct)
    {
        await jobs.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunFfmpeg(ResolveFfmpeg(ffmpeg), init, seg, ct).ConfigureAwait(false);
        }
        finally
        {
            jobs.Release();
        }
    }

    async static Task<byte[]> RunFfmpeg(string ffmpeg, byte[] init, byte[] seg, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "mp4", "-i", "pipe:0",
            "-map", "0:v:0", "-map", "0:a:0?",
            "-c", "copy",
            "-copyts", "-muxdelay", "0", "-muxpreload", "0", "-mpegts_copyts", "1",
            "-f", "mpegts", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using (var p = new Process { StartInfo = psi })
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(40));

            try
            {
                p.Start();

                var errTask = p.StandardError.ReadToEndAsync();
                var output = new MemoryStream(seg.Length + (seg.Length / 8) + 4096);
                var outTask = p.StandardOutput.BaseStream.CopyToAsync(output, cts.Token);

                try
                {
                    var stdin = p.StandardInput.BaseStream;
                    await stdin.WriteAsync(init, cts.Token).ConfigureAwait(false);
                    await stdin.WriteAsync(seg, cts.Token).ConfigureAwait(false);
                    stdin.Close();
                }
                catch
                {
                    // ffmpeg мог завершиться раньше — причина будет в коде выхода и stderr
                }

                await outTask.ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                if (p.ExitCode != 0 || output.Length == 0)
                {
                    string err = await errTask.ConfigureAwait(false);
                    Report($"Mirage remux: ffmpeg exit={p.ExitCode} {err}");
                    return null;
                }

                return output.ToArray();
            }
            catch (Exception ex)
            {
                Report($"Mirage remux: {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(true);
                }
                catch { }
            }
        }
    }
}
