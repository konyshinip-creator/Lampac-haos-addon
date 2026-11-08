using Newtonsoft.Json.Linq;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LampacHub;

/// <summary>
/// Списки профиля одним целым: категории Lampa (в базе Sync) и свои категории модуля
/// («Пересматриваю») в LampacHub.sql. Названия как в Yummy/Anihub:
/// Смотрю — look, Планирую — scheduled, Просмотрено — viewed, Отложено — continued,
/// Брошено — thrown, Пересматриваю — rewatch, Избранное — like.
/// </summary>
public static class HubLists
{
    public const string Rewatch = "rewatch";

    /// <summary>Взаимоисключающие статусы: свои статусы Lampa плюс «Пересматриваю»</summary>
    public static readonly string[] Statuses = SyncBookmarks.Statuses.Concat(new[] { Rewatch }).ToArray();

    public static bool Known(string category)
        => SyncBookmarks.Categories.Contains(category) || HubStore.OwnCategories.Contains(category);

    public static string Area(HubProfile profile)
    {
        var account = HubStore.GetAccount(profile.account_id);
        return DataArea.Compose(HubStore.EffectiveUid(account), profile.id);
    }

    public class Entry
    {
        public string id { get; set; }
        public JObject card { get; set; }
        public List<string> categories { get; set; } = new List<string>();
        public long updated { get; set; }
    }

    public static List<Entry> Entries(HubProfile profile)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);

        foreach (var row in SyncBookmarks.Load(Area(profile)))
        {
            var names = row.Names.Where(n => SyncBookmarks.Categories.Contains(n)).ToList();
            if (names.Count == 0)
                continue;

            map[row.card_id] = new Entry { id = row.card_id, card = row.card ?? new JObject { ["id"] = row.card_id }, categories = names, updated = row.updated_at };
        }

        foreach (var item in HubStore.ListItems(profile.id))
        {
            if (!map.TryGetValue(item.card_id, out var entry))
                map[item.card_id] = entry = new Entry { id = item.card_id, card = item.card };

            if (!entry.categories.Contains(item.category))
                entry.categories.Add(item.category);

            entry.updated = Math.Max(entry.updated, item.added);
        }

        return map.Values.OrderByDescending(e => e.updated).ToList();
    }

    /// <summary>
    /// Добавить и убрать категории у карточки. Новый статус снимает прежний; карточка для новой
    /// записи берётся из переданной, из Sync или из своих списков.
    /// </summary>
    public static async Task Change(HubProfile profile, string id, IEnumerable<string> add, IEnumerable<string> remove, JObject card = null)
    {
        var plus = (add ?? Enumerable.Empty<string>()).Where(Known).Distinct().ToList();
        var minus = (remove ?? Enumerable.Empty<string>()).Where(Known).Except(plus).ToList();

        if (plus.Any(c => Statuses.Contains(c)))
            minus = minus.Union(Statuses.Except(plus)).ToList();

        var own = HubStore.ListItems(profile.id).Where(i => i.card_id == id).ToList();
        JObject known = card ?? own.Select(i => i.card).FirstOrDefault();

        var syncPlus = plus.Where(c => SyncBookmarks.Categories.Contains(c)).ToList();
        var syncMinus = minus.Where(c => SyncBookmarks.Categories.Contains(c)).ToList();

        if (syncPlus.Count > 0 || syncMinus.Count > 0 || card != null)
        {
            await SyncBookmarks.Modify(Area(profile), (rows, stamp) =>
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    if (syncPlus.Count == 0)
                        return;
                    rows[id] = row = new SyncBookmarks.Row { card_id = id, categories = new JObject() };
                }

                known ??= row.card;
                if (card != null || row.card == null)
                    row.SetCard(known);

                foreach (var c in syncMinus)
                    row.Remove(c);
                foreach (var c in syncPlus)
                    row.Add(c, stamp);
            });
        }

        if (known == null && plus.Any(c => HubStore.OwnCategories.Contains(c)))
            known = SyncBookmarks.Load(Area(profile)).FirstOrDefault(r => r.card_id == id)?.card;

        foreach (var c in minus.Where(c => HubStore.OwnCategories.Contains(c)))
            HubStore.SetListItem(profile.id, c, id, null, false);

        foreach (var c in plus.Where(c => HubStore.OwnCategories.Contains(c)))
        {
            if (known != null)
                HubStore.SetListItem(profile.id, c, id, known, true);
        }
    }

    /// <summary>Убрать карточки из всех категорий, включая историю</summary>
    public static async Task<int> RemoveAll(HubProfile profile, ICollection<string> ids)
    {
        if (ids.Count == 0)
            return 0;

        await SyncBookmarks.Modify(Area(profile), (rows, stamp) =>
        {
            foreach (var id in ids)
            {
                if (rows.TryGetValue(id, out var row))
                {
                    foreach (var c in row.Names.ToList())
                        row.Remove(c);
                }
            }
        });

        foreach (var id in ids)
            HubStore.RemoveListCard(profile.id, id);

        return ids.Count;
    }

    /// <summary>Записи для переноса, экспорта и импорта: карточка и её категории</summary>
    public class Portable
    {
        public string id { get; set; }
        public JObject card { get; set; }
        public List<string> categories { get; set; }
    }

    /// <summary>
    /// Влить записи в профиль: категории добавляются к имеющимся, статус из источника заменяет
    /// статус в профиле. Возвращает число записей, которые удалось взять.
    /// </summary>
    public static async Task<int> Merge(HubProfile profile, IEnumerable<Portable> items)
    {
        var list = items
            .Where(i => i != null && !string.IsNullOrEmpty(i.id) && i.card != null && i.categories != null)
            .Select(i => new Portable { id = i.id, card = i.card, categories = i.categories.Where(Known).Distinct().ToList() })
            .Where(i => i.categories.Count > 0)
            .GroupBy(i => i.id).Select(g => g.Last())
            .ToList();

        if (list.Count == 0)
            return 0;

        await SyncBookmarks.Modify(Area(profile), (rows, stamp) =>
        {
            foreach (var item in list)
            {
                var plus = item.categories.Where(c => SyncBookmarks.Categories.Contains(c)).ToList();
                bool status = item.categories.Any(c => Statuses.Contains(c));

                if (!rows.TryGetValue(item.id, out var row))
                {
                    if (plus.Count == 0 && !status)
                        continue;
                    rows[item.id] = row = new SyncBookmarks.Row { card_id = item.id, categories = new JObject() };
                }

                if (row.card == null)
                    row.SetCard(item.card);

                if (status)
                {
                    foreach (var c in SyncBookmarks.Statuses.Except(plus))
                        row.Remove(c);
                }

                foreach (var c in plus)
                    row.Add(c, stamp);
            }
        });

        foreach (var item in list)
        {
            bool status = item.categories.Any(c => Statuses.Contains(c));
            foreach (var c in HubStore.OwnCategories)
            {
                if (item.categories.Contains(c))
                    HubStore.SetListItem(profile.id, c, item.id, item.card, true);
                else if (status && Statuses.Contains(c))
                    HubStore.SetListItem(profile.id, c, item.id, null, false);
            }
        }

        return list.Count;
    }

    public static List<Portable> Export(HubProfile profile, ICollection<string> ids = null)
        => Entries(profile)
            .Where(e => ids == null || ids.Contains(e.id))
            .Select(e => new Portable { id = e.id, card = e.card, categories = e.categories })
            .ToList();
}
