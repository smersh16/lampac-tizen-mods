using Shared.Models.Base;

namespace Phantom;

public class ModuleConf : BaseSettings
{
    public ModuleConf(string plugin, string apihost, string linkhost, string token, string secret_token, bool m4s)
    {
        this.plugin = plugin;
        this.token = token;
        this.secret_token = secret_token;
        this.m4s = m4s;
        this.linkhost = linkhost;
        this.apihost = apihost;
    }


    public string secret_token { get; set; }

    public string linkhost { get; set; }

    public bool m4s { get; set; }

    public bool debug { get; set; }

    /// <summary>Пересобирать fMP4-HLS в MPEG-TS (для ТВ, не тянущих fMP4)</summary>
    public bool remux_ts { get; set; } = true;

    /// <summary>Regex по User-Agent клиентов, которым отдаётся TS. Пусто — всем.</summary>
    public string remux_ua { get; set; } = "Tizen|SMART-TV|SmartTV";

    /// <summary>Максимальная высота (p) для клиентов с TS-ремаксом. 0 — без ограничения.</summary>
    public int remux_maxq { get; set; } = 1080;

    /// <summary>Путь к ffmpeg. Пусто — data/ffmpeg(.exe) или ffmpeg из PATH.</summary>
    public string ffmpeg { get; set; }
}
