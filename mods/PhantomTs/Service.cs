using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom;

public static class Service
{
    const string AuthBearer = "Bearer pXzvbyDGLYyB6VkwsWZDv3iMKZtsXNzpzRyxZUcsKHXxsSeaYakbo3hw9mBFRc5VQTpqAX6BW8aDEqyLaHYcXSQiV6KHYTVTK6MYRphNAy5sBjtrevqkDzKmLqNdfMZGEU9NELjmtKfZy3RNGzCd767sNh1mXEj4tCcvqndHtzmwAbZNkhm4ghDEasodotMBewypNQ56uotJAQGX11csfeRfBAPk8DcUWWkkqzxca8vbnEw12vUFbBzT6hz8ZB3F3dzUhUXoL2cr1WM1bXQArRCS1MUNMz3X5WDMMQoZKxj2AMTRqp7QQX4dDB9B7VzEZTmyFULhm1AcHHMkoMvSVvKYoBoAKLycYAgMHeD4ECJcGEAGpnkJhrV57zQ7";

    /// <summary>
    /// Те же заголовки, что ProxyApiCreateHttpRequest ставит на запросы к CDN,
    /// но списком — для прямых запросов из TsRemux (без /proxy).
    /// </summary>
    public static List<HeadersModel> CdnHeaders(StreamData watch)
    {
        var list = new List<HeadersModel>
        {
            new("sec-ch-ua", Http.defaultUaHeaders["sec-ch-ua"]),
            new("sec-ch-ua-mobile", "?0"),
            new("sec-ch-ua-platform", "\"Windows\""),
            new("User-Agent", Http.UserAgent),
            new("Accept", "*/*"),
            new("Accept-Language", "ru-RU,ru;q=0.9,uk-UA;q=0.8,uk;q=0.7,en-US;q=0.6,en;q=0.5"),
            new("Accepts-Controls", watch.edge_hash),
            new("Authorizations", AuthBearer),
            new("Sec-Fetch-Site", "cross-site"),
            new("Sec-Fetch-Mode", "cors"),
            new("Sec-Fetch-Dest", "empty")
        };

        if (!string.IsNullOrEmpty(watch.requestOrigin))
            list.Add(new("Origin", watch.requestOrigin));

        if (!string.IsNullOrEmpty(watch.requestReferer))
            list.Add(new("Referer", watch.requestReferer));

        return list;
    }

    async public static Task ProxyApiCreateHttpRequest(EventProxyApiCreateHttpRequest e)
    {
        if (e.plugin != null && e.plugin.Equals("phantom", StringComparison.OrdinalIgnoreCase))
        {
            var watch = e.decryptLink?.userdata as StreamData;
            if (string.IsNullOrEmpty(watch?.edge_hash))
            {
                if (ModInit.conf.debug)
                    Console.WriteLine("watch null");

                e.requestMessage.RequestUri = null;
                return;
            }

            // сохраняем Range/If-Range от клиента (иначе CDN отдаёт 200 вместо 206)
            string[] clientRange = e.requestMessage.Headers.TryGetValues("Range", out var rv) ? rv.ToArray() : null;
            string[] clientIfRange = e.requestMessage.Headers.TryGetValues("If-Range", out var irv) ? irv.ToArray() : null;

            e.requestMessage.Headers.Clear();

            e.requestMessage.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            e.requestMessage.Headers.TryAddWithoutValidation("sec-ch-ua", Http.defaultUaHeaders["sec-ch-ua"]);
            e.requestMessage.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            e.requestMessage.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            e.requestMessage.Headers.TryAddWithoutValidation("User-Agent", Http.UserAgent);
            e.requestMessage.Headers.TryAddWithoutValidation("Accept", "*/*");
            e.requestMessage.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,uk-UA;q=0.8,uk;q=0.7,en-US;q=0.6,en;q=0.5");
            e.requestMessage.Headers.TryAddWithoutValidation("Accepts-Controls", watch.edge_hash);
            e.requestMessage.Headers.TryAddWithoutValidation("Authorizations", AuthBearer);
            e.requestMessage.Headers.TryAddWithoutValidation("Origin", watch.requestOrigin);
            e.requestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            e.requestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            e.requestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            e.requestMessage.Headers.TryAddWithoutValidation("Referer", watch.requestReferer);
            e.requestMessage.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br, zstd");

            if (clientRange != null)
                e.requestMessage.Headers.TryAddWithoutValidation("Range", clientRange);
            if (clientIfRange != null)
                e.requestMessage.Headers.TryAddWithoutValidation("If-Range", clientIfRange);

            if (e.requestMessage.Content?.Headers != null)
                e.requestMessage.Content.Headers.Clear();
        }
    }
}
