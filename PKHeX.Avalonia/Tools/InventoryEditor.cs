using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Bag / inventory editor: one tab per pouch, with a list of slots and an editor for the selected slot.
/// </summary>
public static class InventoryEditor
{
    public static bool IsSupported(SaveFile sav) => sav.Inventory.Pouches.Count != 0;

    public static Window Create(SaveFile sav)
    {
        var bag = sav.Inventory;
        var itemNames = (string[])GameInfo.Strings.GetItemStrings(sav.Context, sav.Version).Clone();
        for (int i = 0; i < itemNames.Length; i++)
        {
            if (string.IsNullOrEmpty(itemNames[i]))
                itemNames[i] = $"(Item #{i:000})";
        }

        // Games where every item has a fixed slot; only the counts can be changed.
        bool fixedSlots = sav is SAV9SV or SAV9ZA;
        var item0 = bag.Pouches[0].Items.FirstOrDefault();
        bool hasNew = item0 is IItemNewFlag;

        var tabs = new TabControl();
        foreach (var pouch in bag.Pouches)
        {
            pouch.Sanitize(itemNames.Length - 1);
            tabs.Items.Add(new TabItem { Header = GetPouchName(pouch.Type), Content = CreatePouchView(bag, pouch, itemNames, fixedSlots, hasNew) });
        }

        return Ui.ToolWindow($"Bag — {GameInfo.GetVersionName(sav.Version)}", tabs, () =>
        {
            foreach (var pouch in bag.Pouches)
                Compact(bag, pouch, hasNew, fixedSlots);
            bag.CopyTo(sav);
            sav.State.Edited = true;
        }, 820, 620);
    }

    private static string GetPouchName(InventoryType type) => type switch
    {
        InventoryType.KeyItems => "Key Items",
        InventoryType.TMHMs => "TMs/HMs",
        InventoryType.BattleItems => "Battle Items",
        InventoryType.MailItems => "Mail",
        InventoryType.PCItems => "PC Items",
        InventoryType.FreeSpace => "Free Space",
        InventoryType.ZCrystals => "Z-Crystals",
        _ => type.ToString(),
    };

    private static Control CreatePouchView(PlayerBag bag, InventoryPouch pouch, string[] itemNames, bool fixedSlots, bool hasNew)
    {
        var legal = pouch.GetAllItems().ToArray();
        var choices = new List<ComboItem> { new(itemNames[0], 0) };
        choices.AddRange(legal.Where(z => z != 0 && z < itemNames.Length).Select(z => new ComboItem(itemNames[z], z)).OrderBy(z => z.Text));

        var list = new ListBox { MinWidth = 340 };
        var cbItem = Ui.Combo(choices);
        cbItem.IsEnabled = !fixedSlots;
        var nudCount = Ui.Number(0, Math.Max(pouch.MaxCount, 1));
        var chkNew = new CheckBox { Content = "New", IsVisible = hasNew };
        var chkFav = new CheckBox { Content = "Favorite", IsVisible = pouch.Items.FirstOrDefault() is IItemFavorite };
        var nudAll = Ui.Number(1, Math.Max(pouch.MaxCount, 1), Math.Max(1, Math.Min(pouch.MaxCount, 1)));
        var info = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 };

        void Refresh(int keep = -1)
        {
            var items = pouch.Items;
            var rows = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                var name = it.Index < itemNames.Length ? itemNames[it.Index] : $"(Item #{it.Index})";
                rows[i] = it.Index == 0 && it.Count == 0 ? $"{i + 1,4}. —" : $"{i + 1,4}. {name} × {it.Count}";
            }
            list.ItemsSource = rows;
            list.SelectedIndex = keep;
            info.Text = $"{pouch.Count} item(s) in this pouch · {items.Length} slots · max {pouch.MaxCount} each";
        }

        list.SelectionChanged += (_, _) =>
        {
            var i = list.SelectedIndex;
            if ((uint)i >= pouch.Items.Length)
                return;
            var it = pouch.Items[i];
            Ui.SetValue(cbItem, it.Index);
            nudCount.Value = it.Count;
            chkNew.IsChecked = it is IItemNewFlag { IsNew: true };
            chkFav.IsChecked = it is IItemFavorite { IsFavorite: true };
        };

        var apply = Ui.Button("Set slot", (_, _) =>
        {
            var i = list.SelectedIndex;
            if ((uint)i >= pouch.Items.Length)
                return;
            var id = fixedSlots ? pouch.Items[i].Index : Ui.GetValue(cbItem);
            var count = (int)(nudCount.Value ?? 0);
            if (!bag.IsQuantitySane(pouch.Type, id, ref count, hasNew))
                count = 0;
            var item = pouch.GetEmpty(id, count);
            if (item is IItemNewFlag n)
                n.IsNew = chkNew.IsChecked == true;
            if (item is IItemFavorite f)
                f.IsFavorite = chkFav.IsChecked == true;
            if (fixedSlots)
            {
                // keep any other per-item metadata; just update the count
                pouch.Items[i].Count = count;
                if (pouch.Items[i] is IItemNewFlag n2) n2.IsNew = chkNew.IsChecked == true;
                if (pouch.Items[i] is IItemFavorite f2) f2.IsFavorite = chkFav.IsChecked == true;
            }
            else
            {
                pouch.Items[i] = item;
            }
            Refresh(i);
        });
        var remove = Ui.Button("Clear slot", (_, _) =>
        {
            var i = list.SelectedIndex;
            if ((uint)i >= pouch.Items.Length)
                return;
            if (fixedSlots)
                pouch.Items[i].Count = 0;
            else
                pouch.Items[i] = pouch.GetEmpty();
            Refresh(i);
        });
        var addNew = Ui.Button("Add to first empty slot", (_, _) =>
        {
            if (fixedSlots)
                return;
            var id = Ui.GetValue(cbItem);
            if (id == 0)
                return;
            var index = Array.FindIndex(pouch.Items, z => z.Index == id);
            if (index < 0)
                index = Array.FindIndex(pouch.Items, z => z.Index == 0);
            if (index < 0)
                return;
            var count = (int)(nudCount.Value ?? 1);
            if (!bag.IsQuantitySane(pouch.Type, id, ref count, hasNew))
                return;
            pouch.Items[index] = pouch.GetEmpty(id, Math.Max(1, count));
            Refresh(index);
        });
        addNew.IsVisible = !fixedSlots;

        var giveAll = Ui.Button("Give all items", (_, _) =>
        {
            pouch.GiveAllItems(bag, (int)(nudAll.Value ?? 1));
            Refresh();
        });
        var setAll = Ui.Button("Set count of all", (_, _) =>
        {
            pouch.ModifyAllCount(bag, (int)(nudAll.Value ?? 1));
            Refresh();
        });
        var removeAll = Ui.Button("Remove all", (_, _) =>
        {
            pouch.RemoveAll();
            Refresh();
        });
        var sort = Ui.Button("Sort by name", (_, _) =>
        {
            Compact(bag, pouch, hasNew, fixedSlots);
            if (!fixedSlots)
            {
                pouch.SortByName(itemNames);
                pouch.SortByEmpty();
            }
            Refresh();
        });

        var form = new Ui.Form();
        form.Add("Item", cbItem);
        form.Add("Count", nudCount);
        form.AddWide(Ui.Row(chkNew, chkFav));
        form.AddWide(Ui.Row(apply, remove));
        form.AddWide(addNew);
        form.AddWide(Ui.Header("Whole pouch"));
        form.Add("Count", nudAll);
        form.AddWide(Ui.Row(giveAll, setAll));
        form.AddWide(Ui.Row(removeAll, sort));
        form.AddWide(info);

        Refresh();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320") };
        var side = new ScrollViewer { Content = form.Grid, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(side, 1);
        grid.Children.Add(list);
        grid.Children.Add(side);
        return grid;
    }

    /// <summary>
    /// Removes empty slots (moving them to the end) and sanitizes quantities, matching the Windows editor's save logic.
    /// </summary>
    private static void Compact(PlayerBag bag, InventoryPouch pouch, bool hasNew, bool fixedSlots)
    {
        if (fixedSlots)
            return;
        var items = pouch.Items;
        var kept = new List<InventoryItem>(items.Length);
        foreach (var it in items)
        {
            var count = it.Count;
            if (it.Index <= 0 && !hasNew)
                continue;
            if (!bag.IsQuantitySane(pouch.Type, it.Index, ref count, hasNew))
                continue;
            it.Count = count;
            kept.Add(it);
        }
        for (int i = 0; i < items.Length; i++)
            items[i] = i < kept.Count ? kept[i] : pouch.GetEmpty();
    }
}
