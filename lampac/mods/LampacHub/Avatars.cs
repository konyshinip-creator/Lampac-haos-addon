using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LampacHub;

/// <summary>
/// Аватары профилей. Готовые лежат в папке модуля avatars/ (/hub/avatars/*.svg), загруженные с ПК —
/// в database/LampacHub/avatars (/hub/avatar/*.jpg), чтобы попадать в бэкап вместе с базой.
/// В профиле хранится путь, эмодзи или внешний URL.
/// </summary>
public static class HubAvatars
{
    public const string UploadedPrefix = "/hub/avatar/";
    public const string PresetPrefix = "/hub/avatars/";

    const int MaxBytes = 1_500_000;

    public static string Dir => Path.Combine("database", "LampacHub", "avatars");

    public static List<string> Presets()
    {
        try
        {
            string dir = Path.Combine(ModInit.modpath, "avatars");
            if (!Directory.Exists(dir))
                return new List<string>();

            return Directory.GetFiles(dir, "*.svg")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => PresetPrefix + n)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>data:image/...;base64,... → файл. Тип проверяется по сигнатуре, а не по тому, что написано в data URL.</summary>
    public static string Save(string dataUrl, out string error)
    {
        error = null;

        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.Ordinal))
        {
            error = "Ожидается картинка";
            return null;
        }

        int comma = dataUrl.IndexOf(',');
        if (comma < 0)
        {
            error = "Повреждённые данные картинки";
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
        }
        catch
        {
            error = "Повреждённые данные картинки";
            return null;
        }

        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            error = "Картинка слишком большая";
            return null;
        }

        string ext = Detect(bytes);
        if (ext == null)
        {
            error = "Поддерживаются JPEG, PNG и WebP";
            return null;
        }

        Directory.CreateDirectory(Dir);
        string name = HubStore.RandomId(16) + "." + ext;
        File.WriteAllBytes(Path.Combine(Dir, name), bytes);
        return UploadedPrefix + name;
    }

    /// <summary>Удаляет файл, если аватар был загружен с ПК; готовые и внешние не трогает</summary>
    public static void DeleteUploaded(string avatar)
    {
        if (string.IsNullOrEmpty(avatar) || !avatar.StartsWith(UploadedPrefix, StringComparison.Ordinal))
            return;

        string name = avatar.Substring(UploadedPrefix.Length);
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '.')))
            return;

        try
        {
            string path = Path.Combine(Dir, name);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    static string Detect(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "jpg";

        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "png";

        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return "webp";

        return null;
    }
}
