using System;
using System.Drawing;
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
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

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
        var url = !string.IsNullOrWhiteSpace(device.IconUrl) ? device.IconUrl
                : !string.IsNullOrWhiteSpace(device.DeviceIconUrl) ? device.DeviceIconUrl
                : null;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return null;

        var ext = Path.GetExtension(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".png";
        var rawPath = Path.Combine(CacheDir, Sanitize(device.Did) + ext);

        if (!File.Exists(rawPath))
        {
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(rawPath, bytes);
            }
            catch
            {
                return null;
            }
        }
        return rawPath;
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
        using var bmp = new Bitmap(src);
        using var resized = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, 48, 48);
        }
        using var icon = Icon.FromHandle(resized.GetHicon());
        using var fs = File.Create(dst);
        icon.Save(fs);
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
