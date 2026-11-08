using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace LampacHub;

public class PendingCode
{
    /// <summary>6 цифр, которые устройство показывает на экране</summary>
    public string code { get; set; }

    /// <summary>Секрет, по которому устройство опрашивает результат; в админку не попадает</summary>
    public string key { get; set; }

    public string name { get; set; }
    public string ip { get; set; }
    public string user_agent { get; set; }
    public DateTime created { get; set; }
    public DateTime expires { get; set; }

    /// <summary>Заполняется при подтверждении в админке и отдаётся устройству один раз</summary>
    public string token { get; set; }
    public string device_id { get; set; }
}

/// <summary>
/// Коды входа живут только в памяти: перезапуск сервера просто отменяет незавершённые входы.
/// </summary>
public static class DeviceCodes
{
    const int MaxPending = 30;

    static readonly ConcurrentDictionary<string, PendingCode> _byCode = new();

    public static PendingCode Create(string name, string ip, string userAgent, int ttlMinutes)
    {
        Cleanup();

        if (_byCode.Count >= MaxPending)
            return null;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            string code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
            var now = DateTime.UtcNow;

            var pending = new PendingCode
            {
                code = code,
                key = HubStore.RandomId(32),
                name = HubStore.Trim(name, 80),
                ip = ip ?? string.Empty,
                user_agent = HubStore.Trim(userAgent, 300),
                created = now,
                expires = now.AddMinutes(ttlMinutes)
            };

            if (_byCode.TryAdd(code, pending))
                return pending;
        }

        return null;
    }

    public static PendingCode FindByCode(string code)
    {
        Cleanup();

        if (string.IsNullOrEmpty(code))
            return null;

        code = new string(code.Where(char.IsDigit).ToArray());
        return _byCode.TryGetValue(code, out var p) ? p : null;
    }

    public static PendingCode FindByKey(string key)
    {
        Cleanup();

        if (string.IsNullOrEmpty(key))
            return null;

        return _byCode.Values.FirstOrDefault(p => p.key == key);
    }

    public static void Remove(string code)
        => _byCode.TryRemove(code, out _);

    public static List<PendingCode> Pending()
    {
        Cleanup();
        return _byCode.Values.Where(p => p.token == null).OrderBy(p => p.created).ToList();
    }

    static void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var p in _byCode.Values)
        {
            if (p.expires < now)
                _byCode.TryRemove(p.code, out _);
        }
    }
}
