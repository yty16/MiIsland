using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 设备图标辅助：把云端设备图片（device_list 的 icon 字段）或用户自定义图片，
/// 下载/缓存为本地文件，并转换为 .lnk 可用的 .ico。
/// 所有方法失败安全：任何异常都返回 null / 原图，不向上抛出。
/// </summary>
public static class DeviceImageHelper
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static DeviceImageHelper()
    {
        // 小米 CDN (res.minet.com / mi-img.com) 对无 UA 的请求直接返回 403，
        // 这是此前"没有任何真实设备图片"的根因。加浏览器 UA 才能正常下载。
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    // 设备图标可能的字段名（不同型号 / 版本返回不一），逐个尝试
    private static readonly string[] IconFields = { "icon", "deviceIcon", "headUrl", "headUrl2", "pic", "thumb" };

    private static string CacheDir
    {
        get
        {
            var dir = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory,
                "device_icons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>解析设备自动图标（云端 URL）的本地缓存路径；首次会下载，之后直接读缓存。失败返回 null。</summary>
    public static async Task<string?> GetCloudImagePathAsync(MiCloudDevice device)
    {
        var url = ResolveIconUrl(device);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var ext = Path.GetExtension(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".png";
        var rawPath = Path.Combine(CacheDir, Sanitize(device.Did) + ext);

        if (!File.Exists(rawPath))
        {
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(url);
                if (bytes.Length < 64) return null; // 过小多半是错误页
                await File.WriteAllBytesAsync(rawPath, bytes);
            }
            catch
            {
                return null;
            }
        }
        return rawPath;
    }

    /// <summary>从设备对象里挑出一个可用的图标 URL：依次尝试多个字段，并处理相对路径。</summary>
    private static string? ResolveIconUrl(MiCloudDevice device)
    {
        foreach (var field in IconFields)
        {
            var raw = field switch
            {
                "icon" => device.IconUrl,
                "deviceIcon" => device.DeviceIconUrl,
                "headUrl" => device.HeadUrl,
                "headUrl2" => device.HeadUrl2,
                "pic" => device.PicUrl,
                "thumb" => device.ThumbUrl,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // 相对路径（以 / 开头）补全小米 CDN 域名
            if (raw.StartsWith("/", StringComparison.Ordinal))
            {
                foreach (var host in new[] { "https://res.minet.com", "https://home.mi.com", "https://cdn.cnbj0.fds.api.mi-img.com" })
                {
                    var abs = host + raw;
                    if (Uri.TryCreate(abs, UriKind.Absolute, out _)) return abs;
                }
                continue;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
                return raw;
        }
        return null;
    }

    /// <summary>把本地图片（png/jpg 等）转成 48x48 的 .ico（供桌面快捷方式 .lnk 使用）；已存在直接返回，失败返回原图路径或 null。</summary>
    public static string? EnsureIco(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;
        var dir = Path.GetDirectoryName(imagePath) ?? CacheDir;
        var icoPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath) + ".ico");
        if (File.Exists(icoPath)) return icoPath;
        try
        {
            ConvertToIco(imagePath, icoPath);
            return File.Exists(icoPath) ? icoPath : imagePath;
        }
        catch
        {
            return imagePath;
        }
    }

    private static void ConvertToIco(string src, string dst)
    {
        // 与快捷方式图标相同的坑：Icon.FromHandle().Save() 序列化的是 32-bit DIB，
        // 不带 alpha，透明像素会被 Windows 渲成黑色。改用 PNG-in-ICO 容器，透明通道完整保留。
        using var bmp = new Bitmap(src);
        using var resized = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(bmp, 0, 0, 48, 48);
        }

        using var pngMs = new MemoryStream();
        resized.Save(pngMs, ImageFormat.Png);
        var pngBytes = pngMs.ToArray();

        using var fs = File.Create(dst);
        WritePngInIco(fs, pngBytes, 48);
    }

    /// <summary>把一帧 PNG 字节写成 PNG-in-ICO 容器（Windows Vista+ 原生支持，完整保留 alpha）。</summary>
    private static void WritePngInIco(Stream fs, byte[] pngBytes, int size)
    {
        void WriteU16(ushort v)
        {
            fs.WriteByte((byte)(v & 0xFF));
            fs.WriteByte((byte)((v >> 8) & 0xFF));
        }
        void WriteU32(uint v)
        {
            fs.WriteByte((byte)(v & 0xFF));
            fs.WriteByte((byte)((v >> 8) & 0xFF));
            fs.WriteByte((byte)((v >> 16) & 0xFF));
            fs.WriteByte((byte)((v >> 24) & 0xFF));
        }

        WriteU16(0);          // reserved
        WriteU16(1);          // type = icon
        WriteU16(1);          // image count
        fs.WriteByte((byte)size);   // width (0 means 256, 这里用 48)
        fs.WriteByte((byte)size);   // height
        fs.WriteByte(0);            // color palette
        fs.WriteByte(0);            // reserved
        WriteU16(1);          // color planes
        WriteU16(32);         // bits per pixel
        WriteU32((uint)pngBytes.Length); // PNG data size
        WriteU32(22);         // PNG data offset (ICONDIR 6 + ICONDIRENTRY 16)
        fs.Write(pngBytes, 0, pngBytes.Length);
    }

    // === 类型占位图：设备无云端图、无自定义图时，按类型给不同颜色 + 字形 ===

    /// <summary>类型占位图用的 emoji 字形（Avalonia 在 Windows 上可正确渲染彩色 emoji）。</summary>
    public static string PlaceholderGlyph(MiDeviceKind kind) => kind switch
    {
        MiDeviceKind.Light => "💡",
        MiDeviceKind.Switch => "🔌",
        MiDeviceKind.Sensor => "📡",
        MiDeviceKind.Curtain => "🪟",
        MiDeviceKind.Tv => "📺",
        MiDeviceKind.AirConditioner => "❄️",
        MiDeviceKind.Vacuum => "🤖",
        MiDeviceKind.AirPurifier => "🌬️",
        MiDeviceKind.Fan => "🌀",
        MiDeviceKind.Humidifier => "💧",
        MiDeviceKind.Camera => "🎥",
        MiDeviceKind.Lock => "🔒",
        MiDeviceKind.Router => "📶",
        MiDeviceKind.Speaker => "🔊",
        MiDeviceKind.Kettle => "🫖",
        MiDeviceKind.Heater => "🔥",
        MiDeviceKind.Washer => "🧺",
        MiDeviceKind.Fridge => "🧊",
        MiDeviceKind.Gateway => "🌐",
        _ => "❓"
    };

    /// <summary>类型占位图用的主题色（十六进制）。</summary>
    public static string PlaceholderColor(MiDeviceKind kind) => kind switch
    {
        MiDeviceKind.Light => "#FFB300",
        MiDeviceKind.Switch => "#2196F3",
        MiDeviceKind.Sensor => "#009688",
        MiDeviceKind.Curtain => "#9C27B0",
        MiDeviceKind.Tv => "#3F51B5",
        MiDeviceKind.AirConditioner => "#00BCD4",
        MiDeviceKind.Vacuum => "#607D8B",
        MiDeviceKind.AirPurifier => "#4CAF50",
        MiDeviceKind.Fan => "#03A9F4",
        MiDeviceKind.Humidifier => "#2196F3",
        MiDeviceKind.Camera => "#795548",
        MiDeviceKind.Lock => "#FF5722",
        MiDeviceKind.Router => "#9C27B0",
        MiDeviceKind.Speaker => "#E91E63",
        MiDeviceKind.Kettle => "#FF9800",
        MiDeviceKind.Heater => "#F44336",
        MiDeviceKind.Washer => "#009688",
        MiDeviceKind.Fridge => "#00BCD4",
        MiDeviceKind.Gateway => "#673AB7",
        _ => "#757575"
    };

    /// <summary>类型占位图在 .lnk 图标上绘制的字母（System.Drawing 绘制字母比 emoji 可靠）。</summary>
    public static char PlaceholderLetter(MiDeviceKind kind) => kind switch
    {
        MiDeviceKind.Light => 'L',
        MiDeviceKind.Switch => 'S',
        MiDeviceKind.Sensor => 'R',
        MiDeviceKind.Curtain => 'C',
        MiDeviceKind.Tv => 'T',
        MiDeviceKind.AirConditioner => 'A',
        MiDeviceKind.Vacuum => 'V',
        MiDeviceKind.AirPurifier => 'P',
        MiDeviceKind.Fan => 'F',
        MiDeviceKind.Humidifier => 'H',
        MiDeviceKind.Camera => 'C',
        MiDeviceKind.Lock => 'L',
        MiDeviceKind.Router => 'W',
        MiDeviceKind.Speaker => 'S',
        MiDeviceKind.Kettle => 'K',
        MiDeviceKind.Heater => 'E',
        MiDeviceKind.Washer => 'W',
        MiDeviceKind.Fridge => 'R',
        MiDeviceKind.Gateway => 'G',
        _ => '?'
    };

    /// <summary>生成类型占位 .ico（供无图设备的桌面快捷方式使用），按类型缓存；失败返回 null。</summary>
    public static string? EnsurePlaceholderIco(MiDeviceKind kind, string? name)
    {
        try
        {
            var safe = Sanitize(name ?? kind.ToString());
            var icoPath = Path.Combine(CacheDir, $"ph_{kind}_{safe}.ico");
            if (File.Exists(icoPath)) return icoPath;
            DrawPlaceholderIco(kind, icoPath);
            return File.Exists(icoPath) ? icoPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DrawPlaceholderIco(MiDeviceKind kind, string dst)
    {
        using var bmp = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(PlaceholderColor(kind)));
            g.FillRectangle(brush, 4, 4, 40, 40);
            using var f = new Font("Segoe UI", 22, FontStyle.Bold);
            var s = PlaceholderLetter(kind).ToString();
            var sz = g.MeasureString(s, f);
            using var sb = new SolidBrush(Color.White);
            g.DrawString(s, f, sb, (48 - sz.Width) / 2, (48 - sz.Height) / 2);
        }

        using var pngMs = new MemoryStream();
        bmp.Save(pngMs, ImageFormat.Png);
        var pngBytes = pngMs.ToArray();

        using var fs = File.Create(dst);
        WritePngInIco(fs, pngBytes, 48);
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
