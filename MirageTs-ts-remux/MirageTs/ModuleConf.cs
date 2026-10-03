using Shared.Models.Base;
using System;

namespace Mirage;

public class ModuleConf : BaseSettings, ICloneable
{
    public ModuleConf(string plugin, string apihost, string linkhost, string token, string secret_token, bool localip, bool m4s)
    {
        this.plugin = plugin;
        this.token = token;
        this.secret_token = secret_token;
        this.localip = localip;
        this.m4s = m4s;

        this.linkhost = linkhost == null ? string.Empty : (linkhost.StartsWith("http") ? linkhost : Decrypt(linkhost)!);
        this.apihost = apihost == null ? string.Empty : (apihost.StartsWith("http") ? apihost : Decrypt(apihost));
    }


    public string secret_token { get; set; }

    public string linkhost { get; set; }

    public bool localip { get; set; }

    public bool m4s { get; set; }

    public bool reserve { get; set; }

    public bool debug { get; set; }

    /// <summary>Пересобирать fMP4-HLS в MPEG-TS (для ТВ, не тянущих fMP4)</summary>
    public bool remux_ts { get; set; } = true;

    /// <summary>Regex по User-Agent клиентов, которым отдаётся TS. Пусто — всем.</summary>
    public string remux_ua { get; set; } = "Tizen|SMART-TV|SmartTV";

    /// <summary>Максимальная высота (p) для клиентов с TS-ремаксом; выше 1080 поток обычно AV1. 0 — без ограничения.</summary>
    public int remux_maxq { get; set; } = 1080;

    /// <summary>Путь к ffmpeg. Пусто — data/ffmpeg(.exe) или ffmpeg из PATH.</summary>
    public string ffmpeg { get; set; }


    public ModuleConf Clone()
    {
        return (ModuleConf)MemberwiseClone();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
