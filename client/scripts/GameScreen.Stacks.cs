using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

public partial class GameScreen
{
    private int[] _splitUnitIds = Array.Empty<int>();
    private int _splitTile = -1;

    internal void StackSelectionChanged(int id)
    {
        if (_splitUnitIds.Length != 0 && id != _splitUnitIds[0]) ClearStackOrders();
    }

    private void ClearStackOrders()
    {
        _splitUnitIds = Array.Empty<int>();
        _splitTile = -1;
    }

    private void OpenArmyStack(UnitDto unit)
    {
        var members = Map.DisplayedArmy(unit.Id).Where(W.Units.ContainsKey).Select(id => W.Units[id])
            .Where(u => u.Owner == unit.Owner && u.Tile == unit.Tile).ToArray();
        bool own = unit.Owner == W.MyId;
        if (members.Length < 2) return;

        var panel = new ConfirmationDialog
        {
            Title = $"Войска на клетке · {members.Length}",
            OkButtonText = own ? "Выбрать для приказа" : "Закрыть",
            DialogHideOnOk = false,
        };
        panel.GetCancelButton().Hide();
        var intro = Ui.Lbl(own ? "Отметьте войска, которые должны пойти отдельно." : "Видимые войска на клетке", 14);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(320, Math.Min(400, members.Length * 34)),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(list);
        var choices = new Dictionary<int, CheckBox>();
        foreach (var member in members)
        {
            string stats = $"{member.Def.Name}  ·  {member.Hp}%";
            if (!own) { list.AddChild(Ui.Lbl(stats, 15)); continue; }
            var choice = new CheckBox
            {
                Text = $"{stats}  ·  движение {member.Moves / (float)Rules.MovePoint:0.#}",
                ButtonPressed = member.Id == unit.Id,
            };
            choices.Add(member.Id, choice);
            list.AddChild(choice);
        }
        panel.AddChild(Ui.V(8, intro, scroll));
        AddChild(panel);
        panel.Confirmed += () =>
        {
            if (!own) { panel.QueueFree(); return; }
            var chosen = choices.Where(pair => pair.Value.ButtonPressed && W.Units.TryGetValue(pair.Key, out var current)
                && current.Tile == unit.Tile && current.Owner == W.MyId).Select(pair => pair.Key).ToArray();
            if (chosen.Length == 0) { intro.Text = "Выберите хотя бы один юнит на этой клетке."; return; }
            ClearStackOrders();
            if (CityFocused) Toggle(Win.None);
            Map.Select(chosen[0]);
            _splitUnitIds = chosen;
            _splitTile = unit.Tile;
            Sound.Play(Sfx.Id.Click);
            Toast($"Выбрано: {chosen.Length}. Укажите клетку правой кнопкой.", false);
            panel.QueueFree();
            Refresh();
        };
        panel.Canceled += panel.QueueFree;
        panel.PopupCentered();
    }
}
