using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Models.AppConf;
using Shared.Models.Base;
using Shared.Models.Templates;
using Shared.PlaywrightCore;
using Shared.Services.RxEnumerate;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Mirage;

public class MirageController : BaseOnlineController<ModuleConf>
{
    static IPage page;
    static Timer timer;
    static (string hls, long id_file, string token_movie, int lastseek, DateTime lastreq) curenthsl = new();
    static object locker = new();

    // качество (2160 или нет), с которым открыт текущий плеер; hls текущего плеера в режиме fMP4; имя init-сегмента
    static bool curentUhd;
    static bool tsFmp4;
    static string tsInit;

    static MirageController()
    {
        Directory.CreateDirectory("cache/mirage");
        CoreInit.conf.WAF.limit_map.Insert(0, new WafLimitRootMap("^/lite/mirage/(trans|ts)/", new WafLimitMap { limit = 1000, second = 1 }));

        timer = new Timer(_ =>
        {
            if (page != null && DateTime.Now.AddMinutes(-5) > curenthsl.lastreq)
            {
                try
                {
                    page.CloseAsync();
                    page = null;
                    curenthsl = default;

                    foreach (var file in Directory.GetFiles("cache/mirage"))
                        System.IO.File.Delete(file);
                }
                catch { }
            }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
    }

    public MirageController() : base(ModInit.conf)
    {
        loadKitInitialization = (j, i, c) =>
        {
            if (j.ContainsKey("m4s"))
                i.m4s = c.m4s;
            return i;
        };
    }

    [HttpGet, Staticache(manually: true)]
    [Route("lite/mirage")]
    async public Task<ActionResult> Index(string orid, string imdb_id, long kinopoisk_id, string title, string original_title, byte serial, string original_language, short year, int t = -1, short s = -1, bool origsource = false, bool rjson = false, bool similar = false)
    {
        if (similar)
            return await RouteSpiderSearch(title, origsource, rjson);

        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        var result = await search(orid, imdb_id, kinopoisk_id, title, serial, original_language, year);
        if (result.category_id == 0 || result.data == null)
            return OnError();

        JToken data = result.data;
        string tokenMovie = data["token_movie"] != null ? data.Value<string>("token_movie") : null;
        var frame = await iframe(tokenMovie);
        if (frame.all == null)
            return OnError();

        if (result.category_id is 1 or 3)
        {
            #region Фильм
            var videos = frame.all["theatrical"].ToObject<Dictionary<string, Dictionary<string, JObject>>>();

            var mtpl = new MovieTpl(title, original_title, videos.Count);

            foreach (var i in videos)
            {
                var file = i.Value.First().Value;

                string translation = file.Value<string>("translation");
                string quality = file.Value<string>("quality");
                long id = file.Value<long>("id");
                bool uhd = init.m4s ? file.Value<bool>("uhd") : false;

                string link = $"{host}/lite/mirage/video?id_file={id}&token_movie={data.Value<string>("token_movie")}";
                mtpl.Append(
                    translation,
                    link,
                    "call",
                    accsArgs($"{link.Replace("/video", "/video.m3u8")}&play=true"),
                    voice_name: uhd ? "2160p" : quality,
                    quality: uhd ? "2160p" : ""
                );
            }

            return ContentTpl(mtpl);
            #endregion
        }
        else
        {
            #region Сериал
            string defaultargs = $"&orid={orid}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&year={year}&original_language={original_language}";

            if (s == -1)
            {
                #region Сезоны
                string q = null;

                try
                {
                    if (init.m4s)
                        q = frame.active.Value<bool>("uhd") == true ? "2160p" : null;
                }
                catch { }

                Dictionary<string, JToken> seasons;
                if (frame.all["seasons"] != null)
                    seasons = frame.all["seasons"].ToObject<Dictionary<string, JToken>>();
                else
                    seasons = frame.all.ToObject<Dictionary<string, JToken>>();

                if (seasons.First().Key.StartsWith("t"))
                {
                    var tpl = new SeasonTpl(q);

                    var seasonNumbers = new HashSet<short>();

                    foreach (var translation in seasons)
                    {
                        var file = translation.Value["file"];
                        if (file == null)
                            continue;

                        foreach (var season in file.ToObject<Dictionary<string, object>>())
                        {
                            if (short.TryParse(season.Key, out short seasonNumber))
                                seasonNumbers.Add(seasonNumber);
                        }
                    }

                    if (!seasonNumbers.Any())
                        seasonNumbers.Add(frame.active.Value<short>("seasons"));

                    foreach (short i in seasonNumbers.OrderBy(i => i))
                    {
                        tpl.Append(
                            $"{i} сезон",
                            $"{host}/lite/mirage?rjson={rjson}&s={i}{defaultargs}",
                            i.ToString()
                        );
                    }

                    return ContentTpl(tpl);
                }
                else
                {
                    var tpl = new SeasonTpl(q, seasons.Count);

                    foreach (var season in seasons)
                    {
                        tpl.Append(
                            $"{season.Key} сезон",
                            $"{host}/lite/mirage?rjson={rjson}&s={season.Key}{defaultargs}",
                            season.Key
                        );
                    }

                    return ContentTpl(tpl);
                }
                #endregion
            }
            else
            {
                var vtpl = new VoiceTpl();
                var etpl = new EpisodeTpl();
                var voices = new HashSet<int>();

                string sArhc = s.ToString();

                if (frame.all[sArhc] is JArray)
                {
                    #region Перевод
                    foreach (var episode in frame.all[sArhc])
                    {
                        foreach (var voice in episode.ToObject<Dictionary<string, JObject>>().Select(i => i.Value))
                        {
                            int id_translation = voice.Value<int>("id_translation");
                            if (voices.Contains(id_translation))
                                continue;

                            voices.Add(id_translation);

                            if (t == -1)
                                t = id_translation;

                            vtpl.Append(
                                voice.Value<string>("translation"),
                                t == id_translation,
                                $"{host}/lite/mirage?rjson={rjson}&s={s}&t={id_translation}{defaultargs}"
                            );
                        }
                    }
                    #endregion

                    foreach (var episode in frame.all[sArhc])
                    {
                        foreach (var voice in episode.ToObject<Dictionary<string, JObject>>().Select(i => i.Value))
                        {
                            if (voice.Value<int>("id_translation") != t)
                                continue;

                            string translation = voice.Value<string>("translation");
                            short e = voice.Value<short>("episode");

                            string link = $"{host}/lite/mirage/video?id_file={voice.Value<long>("id")}&token_movie={data.Value<string>("token_movie")}";
                            string streamlink = accsArgs($"{link.Replace("/video", "/video.m3u8")}&play=true");

                            if (e > 0)
                            {
                                etpl.Append(
                                    $"{e} серия",
                                    title ?? original_title,
                                    s,
                                    e,
                                    link,
                                    "call",
                                    voice_name: translation,
                                    streamlink: streamlink
                                );
                            }
                        }
                    }
                }
                else if (frame.all.ToObject<Dictionary<string, object>>().First().Key.StartsWith("t"))
                {
                    #region Перевод
                    foreach (var node in frame.all)
                    {
                        if (!node.First["file"].ToObject<Dictionary<string, object>>().ContainsKey(sArhc))
                            continue;

                        var voice = node.First["file"].First.First.First.First;
                        int id_translation = voice.Value<int>("id_translation");
                        if (voices.Contains(id_translation))
                            continue;

                        voices.Add(id_translation);

                        if (t == -1)
                            t = id_translation;

                        vtpl.Append(
                            voice.Value<string>("translation"),
                            t == id_translation,
                            $"{host}/lite/mirage?rjson={rjson}&s={s}&t={id_translation}{defaultargs}"
                        );
                    }
                    #endregion

                    foreach (var node in frame.all)
                    {
                        foreach (var season in node.First["file"].ToObject<Dictionary<string, object>>())
                        {
                            if (season.Key != sArhc)
                                continue;

                            if (season.Value is JArray sjar)
                            {

                            }
                            else if (season.Value is JObject sjob)
                            {
                                foreach (var episode in sjob.ToObject<Dictionary<string, JObject>>())
                                {
                                    if (episode.Value.Value<int>("id_translation") != t)
                                        continue;

                                    string translation = episode.Value.Value<string>("translation");
                                    short e = episode.Value.Value<short>("episode");

                                    string link = $"{host}/lite/mirage/video?id_file={episode.Value.Value<long>("id")}&token_movie={data.Value<string>("token_movie")}";
                                    string streamlink = accsArgs($"{link.Replace("/video", "/video.m3u8")}&play=true");

                                    if (e > 0)
                                    {
                                        etpl.Append(
                                            $"{e} серия",
                                            title ?? original_title,
                                            s,
                                            e,
                                            link,
                                            "call",
                                            voice_name: translation,
                                            streamlink: streamlink
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    #region Перевод
                    foreach (var episode in frame.all[sArhc].ToObject<Dictionary<string, Dictionary<string, JObject>>>())
                    {
                        foreach (var voice in episode.Value.Select(i => i.Value))
                        {
                            int id_translation = voice.Value<int>("id_translation");
                            if (voices.Contains(id_translation))
                                continue;

                            voices.Add(id_translation);

                            if (t == -1)
                                t = id_translation;

                            vtpl.Append(
                                voice.Value<string>("translation"),
                                t == id_translation,
                                $"{host}/lite/mirage?rjson={rjson}&s={s}&t={id_translation}{defaultargs}"
                            );
                        }
                    }
                    #endregion

                    foreach (var episode in frame.all[sArhc].ToObject<Dictionary<string, Dictionary<string, JObject>>>())
                    {
                        foreach (var voice in episode.Value.Select(i => i.Value))
                        {
                            string translation = voice.Value<string>("translation");
                            if (voice.Value<int>("id_translation") != t)
                                continue;

                            string link = $"{host}/lite/mirage/video?id_file={voice.Value<long>("id")}&token_movie={data.Value<string>("token_movie")}";
                            string streamlink = accsArgs($"{link.Replace("/video", "/video.m3u8")}&play=true");

                            etpl.Append(
                                $"{episode.Key} серия",
                                title ?? original_title,
                                s,
                                episode.Key,
                                link,
                                "call",
                                voice_name: translation,
                                streamlink: streamlink
                            );
                        }
                    }
                }

                etpl.Append(vtpl);

                return ContentTpl(etpl);
            }
            #endregion
        }
    }


    #region Video
    [HttpGet, Staticache(manually: true)]
    [Route("lite/mirage/video")]
    [Route("lite/mirage/video.m3u8")]
    async public Task<ActionResult> Video(long id_file, string token_movie, bool play, string ts = null)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        bool useTs = UseTs(ts) && await MirageRemux.IsAvailable(init.ffmpeg);

        // для ТВ с ремаксом просим у плеера не выше 1080p (2160p — AV1, ТВ его не декодирует)
        bool uhd = init.m4s && !(useTs && init.remux_maxq > 0 && init.remux_maxq < 2160);

        if (init.debug)
        {
            string clientUa = Request.Headers["User-Agent"].ToString();
            Console.WriteLine($"Mirage video: remux_ts={useTs} uhd={uhd} UA={clientUa}");
        }

        string hls = null;

        if (curenthsl.id_file == id_file && curenthsl.token_movie == token_movie && curentUhd == uhd)
            hls = curenthsl.hls;
        else
        {
            hls = await goMovie($"{init.linkhost}/?token_movie={token_movie}&token={init.token}", id_file, uhd);
            if (hls == null)
                return OnError();

            curenthsl = (hls, id_file, token_movie, 0, DateTime.Now);
            curentUhd = uhd;
        }

        if (useTs)
            hls = hls.Replace("/lite/mirage/trans/", "/lite/mirage/ts/");

        if (play)
            return Redirect(hls);

        return ContentTo(VideoTpl.ToJson(
            "play",
            hls,
            "auto",
            vast: init.vast,
            httpContext: HttpContext
        ));
    }

    #region cache helpers
    /// <summary>Ждёт, пока Playwright сохранит файл в cache/mirage; при необходимости перематывает плеер.</summary>
    async Task<string> WaitCacheFile(string fileName)
    {
        curenthsl.lastreq = DateTime.Now;

        string path = $"cache/mirage/{fileName}";
        int.TryParse(Regex.Match(fileName, "seg-([0-9]+)").Groups[1].Value, out int indexSeg);

        if (indexSeg > 20)
        {
            try
            {
                string oldpath = $"cache/mirage/{fileName.Replace($"seg-{indexSeg}", $"seg-{indexSeg - 4}")}";
                if (System.IO.File.Exists(oldpath))
                    System.IO.File.Delete(oldpath);
            }
            catch { }
        }

        var timeout = TimeSpan.FromSeconds(20);
        var sw = Stopwatch.StartNew();

        while (!System.IO.File.Exists(path) && sw.Elapsed < timeout)
        {
            if (indexSeg > 0)
            {
                int seek = (indexSeg * 6) - 10;
                if (seek > 90 && curenthsl.lastseek != seek && page != null)
                {
                    curenthsl.lastseek = seek;

                    try
                    {
                        await page.EvaluateAsync(@"() => 
                                document.getElementById('player').contentWindow.postMessage(
                                  JSON.stringify({
                                    api: ""seek"",
                                    value: " + seek + @"
                                  }),
                                  ""*""
                                );
                            ");
                    }
                    catch { }
                }

                await Task.Delay(4_000);
            }
            else
            {
                await Task.Delay(1_000);
            }
        }

        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Файл мог быть ещё открыт на запись Playwright — повторяем чтение.</summary>
    async static Task<byte[]> ReadBytesRetry(string path)
    {
        for (int i = 0; i < 40; i++)
        {
            try
            {
                return await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        return null;
    }
    #endregion

    [HttpGet]
    [AllowAnonymous]
    [Route("lite/mirage/trans/{fileName}")]
    async public Task<ActionResult> Trans(string fileName)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (!Regex.IsMatch(fileName, "^([a-z0-9\\-]+\\.[a-z0-9]+)$"))
            return BadRequest();

        string path = await WaitCacheFile(fileName);
        if (path == null)
            return NotFound();

        byte[] data = await ReadBytesRetry(path);
        if (data == null)
            return StatusCode(502);

        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        string type = ext switch
        {
            ".m3u8" or ".m3u" => "application/vnd.apple.mpegurl",
            ".m4s" or ".mp4" => "video/mp4",
            ".ts" => "video/MP2T",
            _ => "application/octet-stream"
        };

        return File(data, type, enableRangeProcessing: true);
    }
    #endregion

    #region TS remux
    bool UseTs(string ts)
    {
        if (string.Equals(ts, "false", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(ts, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!init.remux_ts)
            return false;

        if (string.IsNullOrEmpty(init.remux_ua))
            return true;

        return Regex.IsMatch(Request.Headers["User-Agent"].ToString(), init.remux_ua, RegexOptions.IgnoreCase);
    }

    static string BaseName(string uri)
    {
        int q = uri.IndexOfAny(new[] { '?', '#' });
        if (q >= 0)
            uri = uri.Substring(0, q);

        return System.IO.Path.GetFileName(uri);
    }

    static int HeightOf(string inf)
    {
        var m = Regex.Match(inf, @"RESOLUTION=\d+x(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Master: оставляем только варианты, которые плеер Playwright реально загрузил (иначе их нет в кеше).</summary>
    string FilterMaster(string text)
    {
        var header = new List<string>();
        var variants = new List<(string inf, string uri)>();
        string[] lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#EXT-X-I-FRAME"))
                continue;

            if (line.StartsWith("#EXT-X-STREAM-INF"))
            {
                string uri = null;
                while (++i < lines.Length)
                {
                    string next = lines[i].TrimEnd('\r');
                    if (next.Length > 0 && next[0] != '#')
                    {
                        uri = BaseName(next);
                        break;
                    }
                }

                if (uri != null)
                    variants.Add((line, uri));

                continue;
            }

            header.Add(line);
        }

        var keep = variants.Where(v => System.IO.File.Exists($"cache/mirage/{v.uri}")).ToList();

        if (keep.Count == 0 && init.remux_maxq > 0)
            keep = variants.Where(v => HeightOf(v.inf) is var h && (h == 0 || h <= init.remux_maxq)).ToList();

        if (keep.Count == 0)
            keep = variants;

        var sb = new StringBuilder();
        foreach (string h in header)
            sb.Append(h).Append('\n');

        foreach (var v in keep)
            sb.Append(v.inf).Append('\n').Append(v.uri).Append('\n');

        return sb.ToString();
    }

    /// <summary>Media-playlist: убираем EXT-X-MAP, сегменты .m4s -> .ts (их отдаст TsTrans).</summary>
    string RewritePlaylist(string text)
    {
        if (text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            return FilterMaster(text);

        // не fMP4, либо сегменты-диапазоны, либо шифрование — ремакс невозможен, отдаём как есть
        if (!text.Contains("#EXT-X-MAP", StringComparison.Ordinal) ||
            text.Contains("#EXT-X-BYTERANGE", StringComparison.Ordinal) ||
            Regex.IsMatch(text, "#EXT-X-KEY:(?!METHOD=NONE)"))
        {
            tsFmp4 = false;
            return text;
        }

        var map = Regex.Match(text, "#EXT-X-MAP:[^\\n]*URI=\"([^\"]+)\"");
        tsInit = map.Success ? BaseName(map.Groups[1].Value) : null;
        tsFmp4 = true;

        var sb = new StringBuilder();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("#EXT-X-MAP"))
                continue;

            if (line.StartsWith("#EXT-X-VERSION"))
            {
                sb.Append("#EXT-X-VERSION:3\n");
                continue;
            }

            if (line.Length > 0 && line[0] != '#')
            {
                line = BaseName(line);
                if (line.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(0, line.Length - 4) + ".ts";
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("lite/mirage/ts/{fileName}")]
    async public Task<ActionResult> TsTrans(string fileName)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (!Regex.IsMatch(fileName, "^([a-z0-9\\-]+\\.[a-z0-9]+)$"))
            return BadRequest();

        #region плейлисты
        if (fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            string ppath = await WaitCacheFile(fileName);
            if (ppath == null)
                return NotFound();

            byte[] praw = await ReadBytesRetry(ppath);
            if (praw == null)
                return StatusCode(502);

            return Content(RewritePlaylist(Encoding.UTF8.GetString(praw)), "application/vnd.apple.mpegurl");
        }
        #endregion

        if (!fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        // исходное имя в кеше: seg-N-v1-a1.m4s (fMP4) либо тот же .ts, если источник уже отдаёт TS
        string src = tsFmp4 ? fileName.Substring(0, fileName.Length - 3) + ".m4s" : fileName;

        string path = await WaitCacheFile(src);
        if (path == null)
            return NotFound();

        byte[] seg = await ReadBytesRetry(path);
        if (seg == null)
            return StatusCode(502);

        if (!tsFmp4)
            return File(seg, "video/MP2T", enableRangeProcessing: true);

        string initName = tsInit ?? Regex.Replace(src, "^seg-[0-9]+-", "init-").Replace(".m4s", ".mp4");
        string initPath = await WaitCacheFile(initName);
        if (initPath == null)
            return NotFound();

        byte[] initData = await ReadBytesRetry(initPath);
        if (initData == null)
            return StatusCode(502);

        byte[] ts = await MirageRemux.Remux(init.ffmpeg, initData, seg, HttpContext.RequestAborted);
        if (ts == null)
            return StatusCode(502);

        return File(ts, "video/MP2T", enableRangeProcessing: true);
    }
    #endregion


    #region iframe
    async Task<(JToken all, JToken active)> iframe(string token_movie)
    {
        if (string.IsNullOrEmpty(token_movie))
            return default;

        string memKey = $"mirage:iframe:{token_movie}";
        if (!hybridCache.TryGetValue(memKey, out (JToken all, JToken active) cache))
        {
            string json = null;
            string uri = $"{init.linkhost}/?token_movie={token_movie}&token={init.token}";

            await httpHydra.GetSpan(uri, safety: true, addheaders: HeadersModel.Init(
                ("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"),
                ("referer", "https://kinogo-go.tv/"),
                ("sec-fetch-dest", "iframe"),
                ("sec-fetch-mode", "navigate"),
                ("sec-fetch-site", "cross-site"),
                ("upgrade-insecure-requests", "1")
            ),
            spanAction: html =>
            {
                json = Rx.Match(html, "fileList = JSON.parse\\('([^\n\r]+)'\\);");
            });

            if (string.IsNullOrEmpty(json))
                return default;

            try
            {
                var root = JsonConvert.DeserializeObject<JObject>(json);
                if (root == null || !root.ContainsKey("all"))
                    return default;

                cache = (root["all"], root["active"]);

                hybridCache.Set(memKey, cache, cacheTime(40));
            }
            catch { return default; }
        }

        return cache;
    }
    #endregion

    #region goMovie
    async Task<string> goMovie(string uri, long id_file, bool uhd)
    {
        try
        {
            var browser = new PlaywrightBrowser();

            if (page != null)
                await page.CloseAsync();

            try
            {
                foreach (var file in Directory.GetFiles("cache/mirage"))
                    System.IO.File.Delete(file);
            }
            catch { }

            page = await browser.NewPageAsync(init.plugin, proxy: proxy_data, keepopen: false).ConfigureAwait(false);
            if (page == null)
                return default;

            await page.RouteAsync("**/*", async route =>
            {
                try
                {
                    if (route.Request.Url.Contains("kinogo-go.tv"))
                    {
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Body = PlaywrightBase.IframeHtml(uri + "&autoplay")
                        });
                    }
                    else if (route.Request.Url.Contains("/?token_movie="))
                    {
                        var fetchHeaders = route.Request.Headers;
                        fetchHeaders.TryAdd("accept-encoding", "gzip, deflate, br, zstd");
                        fetchHeaders.TryAdd("cache-control", "no-cache");
                        fetchHeaders.TryAdd("pragma", "no-cache");
                        fetchHeaders.TryAdd("sec-fetch-dest", "iframe");
                        fetchHeaders.TryAdd("sec-fetch-mode", "navigate");
                        fetchHeaders.TryAdd("sec-fetch-site", "cross-site");
                        fetchHeaders.TryAdd("sec-fetch-storage-access", "active");

                        var fetchResponse = await route.FetchAsync(new RouteFetchOptions
                        {
                            Url = route.Request.Url,
                            Method = "GET",
                            Headers = fetchHeaders,
                        }).ConfigureAwait(false);

                        string body = await fetchResponse.TextAsync().ConfigureAwait(false);

                        var injected = @"
                                <script>
                                (function() {
                                    localStorage.setItem('allplay', '{""captionParam"":{""fontSize"":""100%"",""colorText"":""Белый"",""colorBackground"":""Черный"",""opacityText"":""100%"",""opacityBackground"":""75%"",""styleText"":""Без контура"",""weightText"":""Обычный текст""},""quality"":" + (uhd ? "2160" : "1080") + @",""volume"":0.5,""muted"":true,""label"":""(Russian) Forced"",""captions"":false}');
                                })();
                                </script>";

                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = fetchResponse.Status,
                            Body = injected + body,
                            Headers = fetchResponse.Headers
                        }).ConfigureAwait(false);
                    }
                    else if (route.Request.Method == "POST" && route.Request.Url.Contains("/movies/"))
                    {
                        string newUrl = Regex.Replace(route.Request.Url, "/[0-9]+$", $"/{id_file}");

                        var fetchHeaders = route.Request.Headers;
                        fetchHeaders.TryAdd("accept-encoding", "gzip, deflate, br, zstd");
                        fetchHeaders.TryAdd("cache-control", "no-cache");
                        fetchHeaders.TryAdd("dnt", "1");
                        fetchHeaders.TryAdd("pragma", "no-cache");
                        fetchHeaders.TryAdd("priority", "u=1, i");
                        fetchHeaders.TryAdd("sec-fetch-dest", "empty");
                        fetchHeaders.TryAdd("sec-fetch-mode", "cors");
                        fetchHeaders.TryAdd("sec-fetch-site", "same-origin");
                        fetchHeaders.TryAdd("sec-fetch-storage-access", "active");

                        var fetchResponse = await route.FetchAsync(new RouteFetchOptions
                        {
                            Url = newUrl,
                            Method = "POST",
                            Headers = fetchHeaders,
                            PostData = route.Request.PostDataBuffer
                        }).ConfigureAwait(false);

                        string json = await fetchResponse.TextAsync().ConfigureAwait(false);

                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = fetchResponse.Status,
                            Body = json,
                            Headers = fetchResponse.Headers
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        if (route.Request.Url.Contains("/stat") || route.Request.Url.Contains("/lists.php"))
                        {
                            await route.AbortAsync();
                            return;
                        }

                        await route.ContinueAsync();
                    }
                }
                catch { }
            });

            TaskCompletionSource<bool> tcsPageResponse = new();

            page.Response += async (s, e) =>
            {
                if (e.Request.Method == "GET")
                {
                    try
                    {
                        if ((e.Url.Contains(".ts") || e.Url.Contains(".m4s")) && !tcsPageResponse.Task.IsCompleted)
                        {
                            tcsPageResponse.SetResult(true);

                            await page.EvaluateAsync(@"() => 
                                    document.getElementById('player').contentWindow.postMessage(
                                      JSON.stringify({
                                        api: ""pause""
                                      }),
                                      ""*""
                                    );
                                ");
                        }
                    }
                    catch { }

                    if (e.Url.Contains(".m3u8") ||
                        e.Url.Contains(".ts") ||
                        e.Url.Contains(".mp4") ||
                        e.Url.Contains(".m4s"))
                    {
                        try
                        {
                            var file = await e.BodyAsync();
                            System.IO.File.WriteAllBytes($"cache/mirage/{Path.GetFileName(e.Url)}", file);
                        }
                        catch { }

                        //if (e.Url.Contains(".ts"))
                        //{
                        //    try
                        //    {
                        //        lock (locker)
                        //        {
                        //            string log = e.Url +
                        //                "\n\n" +
                        //                string.Join("\n", e.Request.Headers.Select(i => i.Key + ": " + i.Value)) +
                        //                "\n\n" +
                        //                string.Join("\n", e.Headers.Select(i => i.Key + ": " + i.Value));

                        //            System.IO.File.AppendAllText("cache/miragelog.txt", log + "\n\n======================\n\n\n");
                        //        }
                        //    }
                        //    catch { }
                        //}
                    }
                }
            };

            PlaywrightBase.GotoAsync(page, "https://kinogo-go.tv/");

            if (await tcsPageResponse.Task.WaitAsync(TimeSpan.FromSeconds(15)))
                return $"{host}/lite/mirage/trans/master.m3u8";
            else
            {
                await page.CloseAsync();
                return default;
            }
        }
        catch
        {
            if (page != null)
                await page.CloseAsync();

            return default;
        }
    }
    #endregion


    #region SpiderSearch
    [HttpGet, Staticache(manually: true)]
    [Route("lite/mirage-search")]
    async public Task<ActionResult> RouteSpiderSearch(string title, bool origsource = false, bool rjson = false)
    {
        if (string.IsNullOrWhiteSpace(title))
            return OnError();

        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        var cache = await InvokeCacheResult<JArray>($"mirage:search:{title}", 40, async e =>
        {
            var root = await httpHydra.Get<JObject>($"{init.apihost}/?token={init.token}&name={HttpUtility.UrlEncode(title)}&list", safety: true);
            if (root == null || !root.ContainsKey("data"))
                return e.Fail("data");

            return e.Success(root["data"].ToObject<JArray>());
        });

        return ContentTpl(cache, () =>
        {
            var stpl = new SimilarTpl(cache.Value.Count);

            foreach (var j in cache.Value)
            {
                stpl.Append(
                    j.Value<string>("name") ?? j.Value<string>("original_name"),
                    j.Value<int>("year").ToString(),
                    string.Empty,
                    $"{host}/lite/mirage?orid={j.Value<string>("token_movie")}",
                    PosterApi.Size(j.Value<string>("poster"))
                );
            }

            return stpl;
        });
    }
    #endregion

    #region search
    async ValueTask<(bool refresh_proxy, int category_id, JToken data)> search(string token_movie, string imdb_id, long kinopoisk_id, string title, int serial, string original_language, int year)
    {
        string memKey = $"mirage:view:{kinopoisk_id}:{imdb_id}";
        if (0 >= kinopoisk_id && string.IsNullOrEmpty(imdb_id))
            memKey = $"mirage:viewsearch:{title}:{serial}:{original_language}:{year}";

        if (!string.IsNullOrEmpty(token_movie))
            memKey = $"mirage:view:{token_movie}";

        JObject root;

        if (!hybridCache.TryGetValue(memKey, out (int category_id, JToken data) res))
        {
            string stitle = title.ToLowerAndTrim();

            if (memKey.Contains(":viewsearch:"))
            {
                if (string.IsNullOrWhiteSpace(title) || year == 0)
                    return default;

                root = await httpHydra.Get<JObject>($"{init.apihost}/?token={init.token}&name={HttpUtility.UrlEncode(title)}&list={(serial == 1 ? "serial" : "movie")}", safety: true);
                if (root == null)
                    return (true, 0, null);

                if (root.ContainsKey("data"))
                {
                    foreach (var item in root["data"])
                    {
                        if (item.Value<string>("name")?.ToLowerAndTrim() == stitle)
                        {
                            int y = item.Value<int>("year");
                            if (y > 0 && (y == year || y == (year - 1) || y == (year + 1)))
                            {
                                if (original_language == "ru" && item.Value<string>("country")?.ToLowerAndTrim() != "россия")
                                    continue;

                                res.data = item;
                                res.category_id = item.Value<int>("category_id");
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                root = await httpHydra.Get<JObject>($"{init.apihost}/?token={init.token}&kp={kinopoisk_id}&imdb={imdb_id}&token_movie={token_movie}", safety: true);
                if (root == null)
                    return (true, 0, null);

                if (root.ContainsKey("data"))
                {
                    res.data = root.GetValue("data");
                    res.category_id = res.data.Value<int>("category");
                }
            }

            if (res.data != null || (root.ContainsKey("error_info") && root.Value<string>("error_info") == "not movie"))
                hybridCache.Set(memKey, res, cacheTime(res.category_id is 1 or 3 ? 120 : 40));
            else
                hybridCache.Set(memKey, res, cacheTime(2));
        }

        return (false, res.category_id, res.data);
    }
    #endregion
}
