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

    /// <summary>
    /// Highest count offered when "above the normal maximum" is enabled (Gen 3 only).
    /// The count is stored as a 16-bit number, but the toss/sell/deposit menus read it as a signed value,
    /// so anything from 32768 up would show as negative there.
    /// </summary>
    public const int Gen3OverLimitMax = 32767;

    /// <summary> Settings shared by all pouch tabs of one bag window. </summary>
    private sealed class Options
    {
        public bool AllowOverLimit { get; set; }
        public bool IsGen3 { get; init; }
        public event Action? Changed;
        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed record Row(int Index, string Text)
    {
        public override string ToString() => Text;
    }

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

        var options = new Options { IsGen3 = sav is SAV3 };
        var tabs = new TabControl();
        foreach (var pouch in bag.Pouches)
        {
            pouch.Sanitize(itemNames.Length - 1, true); // keep counts above the normal maximum that are already in the save
            tabs.Items.Add(new TabItem { Header = GetPouchName(pouch.Type), Content = CreatePouchView(bag, pouch, itemNames, fixedSlots, hasNew, options) });
        }

        Control content = tabs;
        if (sav is SAV3)
        {
            var over = new CheckBox { Content = $"Allow more than the normal maximum (up to {Gen3OverLimitMax:N0})" };
            over.IsCheckedChanged += (_, _) =>
            {
                options.AllowOverLimit = over.IsChecked == true;
                options.RaiseChanged();
            };
            var note = new TextBlock
            {
                Text = "Gen 3 stores counts as a 16-bit number and only enforces 999 when items are added, so a bigger stack works and lasts much longer. " +
                       "It isn't truly infinite (each use still takes one), and the bag shows counts of 1000 or more as \"?\" plus the last two digits, e.g. ×?67.",
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new(0, 0, 0, 6),
            };
            var top = new StackPanel { Children = { over, note } };
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(tabs);
            content = dock;
        }

        return Ui.ToolWindow($"Bag — {GameInfo.GetVersionName(sav.Version)}", content, () =>
        {
            foreach (var pouch in bag.Pouches)
                Compact(bag, pouch, hasNew, fixedSlots, options.IsGen3 && (options.AllowOverLimit || HasOverLimitCounts(pouch)));
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

    private static bool HasOverLimitCounts(InventoryPouch pouch) => pouch.Items.Any(i => i.Index != 0 && i.Count > pouch.MaxCount);

    /// <summary> Validates a count for the pouch, honoring the "above the normal maximum" option. </summary>
    private static bool Sanitize(PlayerBag bag, InventoryPouch pouch, int id, ref int count, bool hasNew, bool overLimit)
    {
        if (!overLimit)
            return bag.IsQuantitySane(pouch.Type, id, ref count, hasNew);
        if (id == 0)
        {
            count = 0;
            return true;
        }
        count = Math.Clamp(count, 0, Gen3OverLimitMax);
        return count != 0 || hasNew;
    }

    private static Control CreatePouchView(PlayerBag bag, InventoryPouch pouch, string[] itemNames, bool fixedSlots, bool hasNew, Options options)
    {
        var legal = pouch.GetAllItems().ToArray();
        var choices = new List<ComboItem> { new(itemNames[0], 0) };
        choices.AddRange(legal.Where(z => z != 0 && z < itemNames.Length).Select(z => new ComboItem(itemNames[z], z)).OrderBy(z => z.Text));

        // Key items, TMs/HMs etc. are single items: no point in going above their normal maximum.
        bool canGoOver = pouch.MaxCount > 1;
        int Max() => options.AllowOverLimit && canGoOver ? Gen3OverLimitMax : Math.Max(pouch.MaxCount, 1);
        bool Over() => options.AllowOverLimit && canGoOver;

        var list = new ListBox { MinWidth = 340, SelectionMode = SelectionMode.Multiple };
        var cbItem = Ui.Combo(choices);
        cbItem.IsEnabled = !fixedSlots;
        var nudCount = Ui.Number(0, Max());
        var chkNew = new CheckBox { Content = "New", IsVisible = hasNew };
        var chkFav = new CheckBox { Content = "Favorite", IsVisible = pouch.Items.FirstOrDefault() is IItemFavorite };
        var nudAll = Ui.Number(1, Max(), 1);
        var info = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 };
        var selectionInfo = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold };
        Row[] rows = [];
        bool loading = false;

        List<int> Selected() => list.SelectedItems is { } items ? [.. items.OfType<Row>().Select(r => r.Index).Order()] : [];

        void Refresh(IReadOnlyCollection<int>? keep = null)
        {
            var items = pouch.Items;
            rows = new Row[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                var name = it.Index < itemNames.Length ? itemNames[it.Index] : $"(Item #{it.Index})";
                rows[i] = new Row(i, it.Index == 0 && it.Count == 0 ? $"{i + 1,4}. —" : $"{i + 1,4}. {name} × {it.Count}");
            }
            loading = true;
            list.ItemsSource = rows;
            if (keep is not null)
            {
                foreach (var i in keep)
                {
                    if ((uint)i < rows.Length)
                        list.SelectedItems?.Add(rows[i]);
                }
            }
            loading = false;
            UpdateSelection();
            info.Text = $"{pouch.Count} item(s) in this pouch · {items.Length} slots · max {Max():N0} each" +
                        (canGoOver ? string.Empty : " (single items)");
        }

        void UpdateSelection()
        {
            var sel = Selected();
            if (sel.Count == 0)
            {
                selectionInfo.Text = "Select a slot (Ctrl/Shift-click to select several).";
                return;
            }
            var first = pouch.Items[sel[0]];
            if (sel.Count == 1)
            {
                selectionInfo.Text = $"Slot {sel[0] + 1} selected";
                cbItem.IsEnabled = !fixedSlots;
                Ui.SetValue(cbItem, first.Index);
            }
            else
            {
                var used = sel.Count(i => pouch.Items[i].Index != 0);
                selectionInfo.Text = $"{sel.Count} slots selected ({used} with an item) — count and flags apply to all of them";
                cbItem.IsEnabled = false; // the same item in several slots would confuse the game
            }
            nudCount.Value = Math.Min(first.Count, Max());
            chkNew.IsChecked = first is IItemNewFlag { IsNew: true };
            chkFav.IsChecked = first is IItemFavorite { IsFavorite: true };
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!loading)
                UpdateSelection();
        };

        options.Changed += () =>
        {
            nudCount.Maximum = Max();
            nudAll.Maximum = Max();
            Refresh(Selected());
        };

        void ApplyFlags(InventoryItem item)
        {
            if (item is IItemNewFlag n)
                n.IsNew = chkNew.IsChecked == true;
            if (item is IItemFavorite f)
                f.IsFavorite = chkFav.IsChecked == true;
        }

        var apply = Ui.Button("Apply to selected", (_, _) =>
        {
            var sel = Selected();
            if (sel.Count == 0)
                return;
            var requested = (int)(nudCount.Value ?? 0);
            if (sel.Count == 1 && !fixedSlots)
            {
                // Single slot: item, count and flags
                var i = sel[0];
                var id = Ui.GetValue(cbItem);
                var count = requested;
                if (!Sanitize(bag, pouch, id, ref count, hasNew, Over()))
                    count = 0;
                var item = pouch.GetEmpty(id, count);
                ApplyFlags(item);
                pouch.Items[i] = item;
            }
            else
            {
                // Several slots (or fixed-slot games): count and flags of the items already there
                foreach (var i in sel)
                {
                    var it = pouch.Items[i];
                    if (it.Index == 0)
                        continue;
                    var count = requested;
                    if (!Sanitize(bag, pouch, it.Index, ref count, hasNew, Over()))
                        count = 0;
                    it.Count = count;
                    ApplyFlags(it);
                }
            }
            Refresh(sel);
        });
        var max = Ui.Button("Max", (_, _) => nudCount.Value = Max());
        var remove = Ui.Button("Clear selected", (_, _) =>
        {
            var sel = Selected();
            foreach (var i in sel)
            {
                if (fixedSlots)
                    pouch.Items[i].Count = 0;
                else
                    pouch.Items[i] = pouch.GetEmpty();
            }
            Refresh(sel);
        });
        var selectItems = Ui.Button("Select all items", (_, _) => Refresh([.. Enumerable.Range(0, pouch.Items.Length).Where(i => pouch.Items[i].Index != 0)]));
        var selectNone = Ui.Button("Select none", (_, _) => Refresh());
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
            var count = Math.Max(1, (int)(nudCount.Value ?? 1));
            if (!Sanitize(bag, pouch, id, ref count, hasNew, Over()))
                return;
            pouch.Items[index] = pouch.GetEmpty(id, Math.Max(1, count));
            Refresh([index]);
        });
        addNew.IsVisible = !fixedSlots;

        var giveAll = Ui.Button("Give all items", (_, _) =>
        {
            pouch.GiveAllItems(bag, Math.Min((int)(nudAll.Value ?? 1), Math.Max(pouch.MaxCount, 1)));
            if (Over())
                SetAllCounts((int)(nudAll.Value ?? 1));
            Refresh();
        });
        void SetAllCounts(int value)
        {
            foreach (var it in pouch.Items)
            {
                if (it.Index == 0)
                    continue;
                var count = value;
                if (Sanitize(bag, pouch, it.Index, ref count, hasNew, Over()))
                    it.Count = count;
            }
        }
        var setAll = Ui.Button("Set count of all", (_, _) =>
        {
            SetAllCounts((int)(nudAll.Value ?? 1));
            Refresh();
        });
        var removeAll = Ui.Button("Remove all", (_, _) =>
        {
            pouch.RemoveAll();
            Refresh();
        });
        var sort = Ui.Button("Sort by name", (_, _) =>
        {
            Compact(bag, pouch, hasNew, fixedSlots, options.IsGen3 && (Over() || HasOverLimitCounts(pouch)));
            if (!fixedSlots)
            {
                pouch.SortByName(itemNames);
                pouch.SortByEmpty();
            }
            Refresh();
        });

        var form = new Ui.Form();
        form.AddWide(selectionInfo);
        form.Add("Item", cbItem);
        form.Add("Count", new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { nudCount, SetColumn(max, 1) },
        });
        form.AddWide(Ui.Row(chkNew, chkFav));
        form.AddWide(Ui.Row(apply, remove));
        form.AddWide(Ui.Row(selectItems, selectNone));
        form.AddWide(addNew);
        form.AddWide(Ui.Header("Whole pouch"));
        form.Add("Count", nudAll);
        form.AddWide(Ui.Row(giveAll, setAll));
        form.AddWide(Ui.Row(removeAll, sort));
        form.AddWide(info);

        Refresh();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        var side = new ScrollViewer { Content = form.Grid, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(side, 1);
        grid.Children.Add(list);
        grid.Children.Add(side);
        return grid;
    }

    private static Control SetColumn(Control c, int column)
    {
        Grid.SetColumn(c, column);
        c.Margin = new(6, 0, 0, 0);
        return c;
    }

    /// <summary>
    /// Removes empty slots (moving them to the end) and sanitizes quantities, matching the Windows editor's save logic.
    /// </summary>
    private static void Compact(PlayerBag bag, InventoryPouch pouch, bool hasNew, bool fixedSlots, bool overLimit)
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
            if (!Sanitize(bag, pouch, it.Index, ref count, hasNew, overLimit && pouch.MaxCount > 1))
                continue;
            it.Count = count;
            kept.Add(it);
        }
        for (int i = 0; i < items.Length; i++)
            items[i] = i < kept.Count ? kept[i] : pouch.GetEmpty();
    }
}
