using Shared;
using Shared.Models.Base;
using Shared.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom;

/// <summary>
/// Сессия ремакса: разобранный media-playlist источника (fMP4/CMAF) + всё нужное для запросов к CDN.
/// </summary>
public class TsSession
{
    public string id;
    public string initUrl;
    public string[] segments;
    public double[] durations;
    public StreamData watch;
    public WebProxy proxy;
    public DateTime expires;

    public byte[] init;
    public readonly SemaphoreSlim initLock = new(1, 1);
}

/// <summary>
/// Пересборка HLS на fMP4 (EXT-X-MAP + .m4s) в HLS на MPEG-TS без перекодирования.
/// Каждый сегмент переупаковывается отдельно: init.mp4 + segment.m4s -> ffmpeg -c copy -> .ts.
/// Таймстемпы сохраняются (-copyts), поэтому сегменты стыкуются, а перемотка работает.
/// </summary>
public static class TsRemux
{
    static readonly ConcurrentDictionary<string, TsSession> sessions = new();

    // не больше N одновременных ffmpeg
    static readonly SemaphoreSlim jobs = new(4, 4);

    static readonly TimeSpan sessionTtl = TimeSpan.FromHours(6);

    // Serilog в Lampac пишет только Error в logs/app-*.log, поэтому ошибки дублируем в консоль (docker logs)
    static void Report(string msg)
    {
        Console.WriteLine(msg);
        Serilog.Log.Error(msg);
    }

    #region ffmpeg
    static readonly ConcurrentDictionary<string, bool> ffmpegChecked = new();

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

        if (ffmpegChecked.TryGetValue(path, out bool ok))
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
            Report($"Phantom: ffmpeg не найден ({path}), ремакс fMP4->TS отключён. Укажите Phantom.ffmpeg в init.conf");

        // положительный результат кешируем навсегда, отрицательный пересмотрится после перезапуска
        ffmpegChecked[path] = ok;
        return ok;
    }
    #endregion

    #region Session
    public static bool TryGet(string id, out TsSession session)
    {
        if (!string.IsNullOrEmpty(id) && sessions.TryGetValue(id, out session))
        {
            if (session.expires > DateTime.UtcNow)
                return true;

            sessions.TryRemove(id, out _);
        }

        session = null;
        return false;
    }

    /// <summary>
    /// Скачивает плейлист (при master — первый вариант), разбирает его.
    /// null — если поток нельзя/не нужно ремаксить (уже TS, byte-range, шифрование, ошибка загрузки).
    /// </summary>
    async public static Task<TsSession> Create(string url, StreamData watch, WebProxy proxy)
    {
        CleanupExpired();

        var headers = Service.CdnHeaders(watch);

        string baseUrl = url;
        string text = await GetText(baseUrl, headers, proxy).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
            return null;

        // master -> берём первый вариант
        if (text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            string variant = FirstUriAfter(text, "#EXT-X-STREAM-INF");
            if (variant == null)
                return null;

            baseUrl = Resolve(baseUrl, variant);
            text = await GetText(baseUrl, headers, proxy).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
                return null;
        }

        // не fMP4 — ремакс не нужен
        if (!text.Contains("#EXT-X-MAP", StringComparison.Ordinal))
            return null;

        // сегменты-диапазоны одного файла и шифрование не поддерживаем
        if (text.Contains("#EXT-X-BYTERANGE", StringComparison.Ordinal))
            return null;

        if (Regex.IsMatch(text, "#EXT-X-KEY:(?!METHOD=NONE)"))
            return null;

        string initUrl = null;
        var segs = new List<string>();
        var durs = new List<double>();
        double pending = 0;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var m = Regex.Match(line, "URI=\"([^\"]+)\"");
                if (m.Success)
                    initUrl = Resolve(baseUrl, m.Groups[1].Value);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string d = line.Substring(8);
                int comma = d.IndexOf(',');
                if (comma >= 0)
                    d = d.Substring(0, comma);

                double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out pending);
            }
            else if (line[0] != '#')
            {
                segs.Add(Resolve(baseUrl, line));
                durs.Add(pending > 0 ? pending : 6);
                pending = 0;
            }
        }

        if (initUrl == null || segs.Count == 0)
            return null;

        var session = new TsSession
        {
            id = Guid.NewGuid().ToString("N"),
            initUrl = initUrl,
            segments = segs.ToArray(),
            durations = durs.ToArray(),
            watch = watch,
            proxy = proxy,
            expires = DateTime.UtcNow.Add(sessionTtl)
        };

        sessions[session.id] = session;
        return session;
    }

    static void CleanupExpired()
    {
        var now = DateTime.UtcNow;

        foreach (var s in sessions)
        {
            if (s.Value.expires <= now)
                sessions.TryRemove(s.Key, out _);
        }
    }

    static string FirstUriAfter(string text, string tag)
    {
        bool take = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(tag, StringComparison.Ordinal))
            {
                take = true;
                continue;
            }

            if (take && line.Length > 0 && line[0] != '#')
                return line;
        }

        return null;
    }

    static string Resolve(string baseUrl, string rel)
    {
        try
        {
            return new Uri(new Uri(baseUrl), rel).AbsoluteUri;
        }
        catch
        {
            return rel;
        }
    }

    async static Task<string> GetText(string url, List<HeadersModel> headers, WebProxy proxy)
    {
        var (data, _) = await Http.BaseDownload(
            url,
            headers: headers,
            proxy: proxy,
            useDefaultHeaders: false,
            timeoutSeconds: 20,
            MaxResponseContentBufferSize: 5_000_000
        ).ConfigureAwait(false);

        if (data == null)
            return null;

        return Encoding.UTF8.GetString(data);
    }
    #endregion

    #region Segment
    async static Task<byte[]> GetInit(TsSession s)
    {
        if (s.init != null)
            return s.init;

        await s.initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s.init == null)
            {
                var (data, _) = await Http.BaseDownload(
                    s.initUrl,
                    headers: Service.CdnHeaders(s.watch),
                    proxy: s.proxy,
                    useDefaultHeaders: false,
                    timeoutSeconds: 30,
                    MaxResponseContentBufferSize: 10_000_000
                ).ConfigureAwait(false);

                s.init = data;
            }

            return s.init;
        }
        finally
        {
            s.initLock.Release();
        }
    }

    async public static Task<byte[]> GetSegment(TsSession s, int index, string ffmpeg, CancellationToken ct)
    {
        byte[] init = await GetInit(s).ConfigureAwait(false);
        if (init == null)
            return null;

        var (seg, _) = await Http.BaseDownload(
            s.segments[index],
            headers: Service.CdnHeaders(s.watch),
            proxy: s.proxy,
            useDefaultHeaders: false,
            timeoutSeconds: 40,
            MaxResponseContentBufferSize: 80_000_000
        ).ConfigureAwait(false);

        if (seg == null)
            return null;

        await jobs.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Remux(ResolveFfmpeg(ffmpeg), init, seg, ct).ConfigureAwait(false);
        }
        finally
        {
            jobs.Release();
        }
    }
    #endregion

    #region Remux
    async static Task<byte[]> Remux(string ffmpeg, byte[] init, byte[] seg, CancellationToken ct)
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
                    // ffmpeg мог завершиться раньше — причина будет в stderr/коде выхода
                }

                await outTask.ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                if (p.ExitCode != 0 || output.Length == 0)
                {
                    string err = await errTask.ConfigureAwait(false);
                    Report($"Phantom remux: ffmpeg exit={p.ExitCode} {err}");
                    return null;
                }

                return output.ToArray();
            }
            catch (Exception ex)
            {
                Report($"Phantom remux: {ex.Message}");
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
    #endregion
}
