using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public partial class GameScreen
{
    // ----------------------------------------------------------- windows
    private float _wrapW = 760;
    private readonly VBoxContainer _progressionSummary = Ui.V();

    public void OpenCity(int id)
    {
        if (_cityId != id) _citizenSource = -1;
        _cityId = id;
        Toggle(Win.City, true);
        // keep the city and its fields in the part of the map the panel leaves free
        if (W.Cities.TryGetValue(id, out var c))
        {
            float free = Size.X - CityW - 40;
            if (!Map.Project(c.Tile, 0, out var at) || at.X > free - 220 || at.X < 240 || at.Y < 160 || at.Y > Size.Y - 200)
                Map.CenterOn(c.Tile, new Vector2(Size.X / 2 - (free + 226) / 2, 0));
        }
    }

    private void CycleCity(int dir)
    {
        var mine = W.Cities.Values.Where(c => c.Owner == W.MyId).OrderBy(c => c.Id).ToList();
        if (mine.Count == 0) return;
        int i = Math.Max(0, mine.FindIndex(c => c.Id == _cityId));
        var next = mine[(i + dir + mine.Count) % mine.Count];
        Map.CenterOn(next.Tile);
        OpenCity(next.Id);
    }

    public void Toggle(Win w, bool forceOpen = false)
    {
        if (w == Win.Branches && !HasBranchAdvisor) return;
        _win = w == _win && !forceOpen ? Win.None : w;
        if (_win != Win.City) _citizenSource = -1;
        _windowScroll.ScrollVertical = 0;
        _windowScroll.ScrollHorizontal = 0;
        _cityScroll.ScrollVertical = 0;
        _scrollToFrontier = true;
        RebuildWindow();
        Refresh();
    }

    private void RebuildWindow()
    {
        bool city = _win == Win.City, advisor = _win != Win.None && !city;
        _window.Visible = advisor;
        _backdrop.Visible = advisor;
        _cityDock.Visible = city;
        Map.FocusCity(city ? W.Cities.GetValueOrDefault(_cityId) : null);
        if (_aiming == null) Map.Highlight = Array.Empty<int>(); // keep the spell's range markers while a target is being picked
        Map.Invalidate();
        if (_win == Win.None) return;
        if (city)
        {
            int at = _cityScroll.ScrollVertical;
            _body = _cityBody;
            _wrapW = CityW - 14;
            Ui.Clear(_cityBody);
            BuildCity();
            _cityScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, at);
            return;
        }
        _body = _windowBody;
        _wrapW = _bodyW - 28;
        Ui.Clear(_windowTabs);
        foreach (var a in Advisors.Where(a => a.win != Win.Branches || HasBranchAdvisor))
        {
            var win = a.win;
            _windowTabs.AddChild(Ui.Btn(a.title, () => Toggle(win, true), $"{a.tip} ({a.key})", 13, a.win == _win ? "TabOn" : "Tab"));
            if (a.win == _win) _windowIcon.Set(a.icon, a.color);
        }
        int scroll = _windowScroll.ScrollVertical, scrollX = _windowScroll.ScrollHorizontal;
        Ui.Clear(_windowBody);
        Ui.Clear(_progressionSummary);
        _progressionSummary.Visible = false;
        switch (_win)
        {
            case Win.Science: BuildScience(); break;
            case Win.Economy: BuildEconomy(); break;
            case Win.Diplomacy: BuildDiplomacy(); break;
            case Win.Demographics: BuildDemographics(); break;
            case Win.Civics: BuildCivics(); break;
            case Win.Religion: BuildReligion(); break;
            case Win.Branches: BuildBranches(); break;
            case Win.Help: BuildHelp(); break;
        }
        if (!_scrollToFrontier || (_win != Win.Science && _win != Win.Civics)) _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, scrollX);
        _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
    }

    private void Add(Control c)
    {
        // long explanations wrap instead of stretching the window sideways
        if (c is Label { Text.Length: > 60 } l && l.AutowrapMode == TextServer.AutowrapMode.Off)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(Math.Min(_wrapW, 900), 0);
            l.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        }
        _body.AddChild(c);
    }
    private void Heading(string text) => Add(Ui.Section(text));
    private void Note(string text) => Add(Ui.Wrap(text, Math.Min(_wrapW, 900), 12, Ui.Dim));

    private void BuildCity()
    {
        if (CitizenCity is not { Detail: { } d } c) { _win = Win.None; _cityDock.Visible = false; return; }
        if (_citizenSource >= 0 && _citizenSource != c.Tile && !d.Worked.Contains(_citizenSource)) _citizenSource = -1;
        Map.Highlight = CitizenHighlights(c, d);
        _cityTitle.Text = (c.Capital ? "★ " : "") + c.Name;
        var me = W.Me;
        Add(Ui.Card(Ui.Flow(14,
            new Chip(Ico.Food, Ui.Food, "Пища за ход").Set(Ui.Signed(d.FoodRate)),
            new Chip(Ico.Prod, Ui.Prod, "Производство за ход").Set(Ui.Signed(d.ProdRate)),
            new Chip(Ico.Science, Ui.Science, $"Наука за ход · коммерция {d.Commerce}").Set(Ui.Signed(d.Science)),
            new Chip(Ico.Gold, Ui.Gold, "Золото и содержание за ход").Set(Ui.Signed(d.Gold), d.Maint > 0 ? $"−{d.Maint}" : ""),
            new Chip(Ico.Culture, Ui.Culture, "Культура за ход").Set(Ui.Signed(d.CultureRate))), 9));

        bool housingFull = c.Pop >= d.HousingCapacity || d.EmpireHousingFree <= 0;
        string growth = d.FoodRate < 0 ? "Голод" : housingFull ? "Нужно жильё"
            : d.FoodRate > 0 ? $"Рост через {Turns(d.FoodNeed - d.Food, d.FoodRate)}" : "Нет роста";
        var grow = Ui.V(4,
            Ui.H(6, new Icon(Ico.Pop, Ui.Food, 14), Ui.Lbl($"Жители {c.Pop}/{d.HousingCapacity}", 13, null, true),
                Ui.Spacer(), Ui.Lbl(growth, 12, d.FoodRate < 0 ? Ui.Bad : housingFull ? Ui.Gold : Ui.Dim)),
            new Bar(d.FoodRate < 0 ? Ui.Bad : Ui.Food, 6).Set(Math.Clamp(d.Food / (float)Math.Max(1, d.FoodNeed), 0, 1),
                housingFull ? 0 : Math.Max(0, d.FoodRate) / (float)Math.Max(1, d.FoodNeed)));
        int from = Rules.CultureForRadius(c.Radius), to = Rules.CultureForRadius(c.Radius + 1);
        bool maxed = c.Radius >= 4;
        var borders = Ui.V(4,
            Ui.H(6, new Icon(Ico.Culture, Ui.Culture, 14), Ui.Lbl($"Границы: радиус {c.Radius}", 13, null, true), Ui.Spacer(),
                Ui.Lbl(maxed ? "Наибольший" : $"{d.Culture}/{to} · {Turns(to - d.Culture, d.CultureRate)}", 12, Ui.Dim)),
            new Bar(Ui.Culture, 6).Set(maxed ? 1 : (d.Culture - from) / (float)Math.Max(1, to - from),
                maxed ? 0 : d.CultureRate / (float)Math.Max(1, to - from)));
        var moodColor = d.Unhappy > 0 ? Ui.Gold : Ui.Good;
        var mood = Ui.V(4,
            Ui.H(6, new Icon(Ico.Happy, moodColor, 14), Ui.Lbl("Довольство", 13, null, true), Ui.Spacer(),
                Ui.Lbl($"{c.Pop - d.Unhappy}/{c.Pop}", 12, moodColor)),
            new Bar(moodColor, 6).Set((c.Pop - d.Unhappy) / (float)Math.Max(1, c.Pop)));
        mood.TooltipText = "Довольные жители работают на клетках города.";
        var housing = Ui.Lbl($"Жильё державы: {d.EmpireHousingCapacity - d.EmpireHousingFree}/{d.EmpireHousingCapacity}" +
            (d.EmpireHousingFree < 0 ? $" · не хватает {-d.EmpireHousingFree}" : ""), 12, d.EmpireHousingFree > 0 ? Ui.Dim : Ui.Gold);
        housing.TooltipText = "Жители, боевые юниты и разведчики занимают по одному месту.";
        Add(Ui.Card(Ui.V(8, grow, borders, mood, housing), 9));

        var auto = Ui.Btn("Авто", () =>
        {
            _citizenSource = -1;
            Net.SendCommand(CmdType.AssignCitizen, c.Id, -1, -1);
            Map.Highlight = d.Worked;
            Map.Invalidate();
            RebuildWindow();
        }, "Вернуть автоматическое распределение жителей", 12, "Flat");
        auto.Disabled = !d.ManualCitizens && _citizenSource < 0;
        Add(Ui.H(8, Ui.Lbl(_citizenSource < 0 ? "Выберите жителя на карте" : "Выберите свободную клетку", 12,
            _citizenSource < 0 ? Ui.Dim : Ui.Good), Ui.Spacer(), auto));
        if (d.Buffs.Length > 0) Add(Ui.Wrap(string.Join(" · ", d.Buffs), _wrapW - 4, 12, Ui.Good));

        Heading("Производство");
        var projectBg = Ui.Col(0x182031);
        if (d.Queue.Length == 0)
            Add(Ui.Card(Ui.Lbl("Нет проекта в очереди", 13, Ui.Dim), 8, projectBg));
        for (int i = 0; i < d.Queue.Length; i++)
        {
            int position = i;
            string item = d.Queue[i];
            int cost = ProjectCost(item, d);
            var title = Ui.H(6, Ui.Lbl($"{i + 1}.", 12, Ui.Dim), Ui.Lbl(Rules.ItemName(item), 13, Ui.Text, i == 0),
                Ui.Spacer(), Ui.Btn("×", () => Net.SendCommand(CmdType.RemoveQueue, c.Id, position), "Убрать из очереди", 12, "Flat"));
            var project = Ui.V(4, title);
            if (i == 0 && cost > 0)
            {
                int rate = Rate(item);
                project.AddChild(new Bar(Ui.Prod, 6).Set(Math.Min(d.Prod, cost) / (float)cost, rate / (float)cost));
                string? blocked = BlockingReason(item);
                if (blocked != null && item.StartsWith("u:", StringComparison.Ordinal) && d.EmpireHousingFree <= 0)
                    blocked += ". Купите дом из списка или уберите проект из начала очереди.";
                project.AddChild(Ui.Wrap(blocked ?? $"{Math.Min(d.Prod, cost)}/{cost} · {Turns(cost - d.Prod, rate)}",
                    _wrapW - 26, 12, blocked == null ? Ui.Dim : Ui.Gold));
            }
            Add(Ui.Card(project, 7, projectBg));
        }

        foreach (var house in Housing.Materials)
        {
            int count = c.Houses.ElementAtOrDefault((int)house.Material);
            int places = d.HousePlaces.ElementAtOrDefault((int)house.Material);
            string tip = $"+{(places > 0 ? places : house.Places)} места · {Rules.Tech(house.Tech)?.Name}. {house.Notes}";
            Option(house.Item, house.Name + (count > 0 ? $" ×{count}" : ""), tip, Ico.City, Ui.Gold);
        }
        foreach (var unit in Rules.Units.Where(unit => unit.Role != UnitRole.Missionary && !unit.X.SummonOnly &&
            (W.Knows(unit.Tech) || d.Queue.Contains("u:" + unit.Id)) && (!unit.Sea || CoastalCity(c))).OrderBy(unit => unit.Cost))
        {
            string tip = unit.Role switch
            {
                UnitRole.Settler => "Основывает город. При найме расходуется один житель.",
                UnitRole.Worker => unit.Sea ? "Работает на воде." : "Строит улучшения и дороги.",
                _ => $"Сила {unit.Str} · передвижение {unit.Moves}",
            };
            Option("u:" + unit.Id, unit.Name, tip, UnitIcon(unit), Rules.IsStacked(unit) ? Ui.Text : Ui.Good);
        }
        foreach (var building in Rules.Buildings.Where(b => d.Buildings.Contains(b.Id) ||
            W.Knows(b.Tech) && b.Id != "palace" && (!b.Coastal || CoastalCity(c))))
            Option("b:" + building.Id, building.Name, building.Describe(), Ico.City, Ui.Text, d.Buildings.Contains(building.Id));
        Option(Rules.Wealth, "Богатство", "Превращает производство в золото.", Ico.Gold, Ui.Gold);

        int Rate(string item) => d.ProdRate * (item == "h:wood" && d.HasLumberMill ? 2 : 1);
        string? BlockingReason(string item)
        {
            if (Housing.Project(item) is { } house)
            {
                if (!W.Knows(house.Tech)) return $"Нужно изучить: {Rules.Tech(house.Tech)?.Name}";
                if (house.Material == HouseMaterial.Stone && !d.HasQuarry) return "Нужна действующая каменоломня";
            }
            int type = item.StartsWith("u:", StringComparison.Ordinal) ? Rules.UnitIndex(item[2..]) : -1;
            if (type < 0) return null;
            var def = Rules.Units[type];
            if (!HasRes(def.Needs)) return $"Нужен ресурс: {Rules.Res(def.Needs).Name}";
            if (Rules.IsStacked(def) && d.EmpireHousingFree <= 0) return "В державе нет свободного жилья";
            if (!W.HasUnitSpace(c.Owner, c.Tile, type)) return "На клетке города нет места";
            if (def.Role == UnitRole.Settler && c.Pop < 2) return "Нужно как минимум 2 жителя";
            return null;
        }

        void Option(string item, string name, string tip, Ico icon, Color color, bool built = false)
        {
            int cost = ProjectCost(item, d), index = Array.IndexOf(d.Queue, item);
            int progress = index == 0 ? d.Prod : 0;
            string? reason = built ? "Построено" : BlockingReason(item);
            if (reason != null) tip = reason + "\n" + tip;
            var row = Ui.H(4);
            var queue = Ui.RowBtn(built ? "✓ " + name : name, index >= 0 ? $"#{index + 1}" : "",
                () => Net.SendCommand(CmdType.SetProduction, c.Id, 1, 0, item), tip, icon, built ? Ui.Good : color, 12);
            if (built) queue.AddThemeColorOverride("font_disabled_color", Ui.Good);
            queue.Disabled = built || reason != null || d.Queue.Length >= Rules.MaxQueue ||
                index >= 0 && (item.StartsWith("b:", StringComparison.Ordinal) || item == Rules.Wealth);
            queue.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(queue);
            if (cost > 0 && !built)
            {
                int gold = Rules.BuyPrice(cost, progress), faith = Rules.FaithPrice(cost, progress);
                row.AddChild(Purchase(Ico.Gold, Ui.Gold, gold, me.Gold, CmdType.Buy, "золото"));
                row.AddChild(Purchase(Ico.Faith, Ui.Faith, faith, me.Faith, CmdType.BuyFaith, "веру"));
                var duration = Ui.H(3, new Icon(Ico.Prod, Ui.Prod, 13), Ui.Lbl(Turns(cost - progress, Rate(item)), 12, Ui.Dim));
                duration.CustomMinimumSize = new Vector2(58, 0);
                duration.TooltipText = $"{cost} производства · {Rate(item)} за ход";
                row.AddChild(duration);
            }
            Add(index >= 0 || built ? Ui.Card(row, 2, projectBg) : row);

            Button Purchase(Ico currencyIcon, Color currencyColor, int price, int available, CmdType command, string currency)
            {
                var button = Ui.RowBtn(price.ToString(), "", () => Net.SendCommand(command, c.Id, 0, 0, item),
                    reason ?? $"Купить сразу за {currency}: {price}", currencyIcon, currencyColor, 12);
                button.CustomMinimumSize = new Vector2(60, 29);
                button.Disabled = reason != null || price <= 0 || available < price;
                return button;
            }
        }
    }

    private static int ProjectCost(string item, CityDetail detail) => Housing.Project(item) is { } house
        ? Math.Max(1, detail.HouseCosts.ElementAtOrDefault((int)house.Material) is > 0 and var cost ? cost : house.Cost)
        : Rules.ItemCost(item);

    internal static string Unlocks(TechDef t)
    {
        var parts = new List<string>();
        parts.AddRange(Rules.Units.Where(u => u.Tech == t.Id).Select(u => u.Name));
        parts.AddRange(Rules.Buildings.Where(b => b.Tech == t.Id).Select(b => b.Name));
        parts.AddRange(Housing.Materials.Where(h => h.Tech == t.Id).Select(h => h.Name));
        foreach (var k in Rules.AllWork)
            if (Rules.WorkTech(k) == t.Id) parts.Add(Rules.WorkName(k).ToLower() + " (улучшение)");
        parts.AddRange(Rules.Resources.Where(r => r.Reveal == t.Id).Select(r => "ресурс " + r.Name.ToLower()));
        if (t.Id == "sailing") parts.Add("посадка на суда у берега");
        if (t.Id == "astronomy") parts.Add("плавание через океан");
        if (t.Branch >= 0) parts.Add(Rules.FutureBranchEffects[t.Branch]);
        return parts.Count == 0 ? "путь к следующим технологиям" : string.Join(", ", parts);
    }

    private void BuildScience()
    {
        var me = W.Me;
        _windowTitle.Text = "Наука и дерево технологий";
        var cur = Rules.Tech(me.Research);
        Add(Ui.Lbl(cur == null ? "Выберите технологию, чтобы потратить накопленную науку." : $"Сейчас изучается: {cur.Name}", 15, cur == null ? Ui.Bad : Ui.Science));
        Add(Ui.Lbl($"Наука за ход: +{me.SciRate}" + (me.EchoBonus > 0 ? $" (из них «научное эхо» от контактов: +{me.EchoBonus})" : "") +
                   $" · изучено технологий: {me.Known.Length + me.Future.Sum()}", 14));
        if (me.Future.Any(f => f > 0))
            Add(Ui.Lbl("Технологии будущего: " + string.Join(", ", me.Future.Select((lv, b) => $"{Rules.FutureBranchNames[b]} {lv}")), 13, Ui.Dim));
        BuildProgressionPlan(me.ResearchPlan, CmdType.SetResearch, id => Rules.Tech(id)?.Name ?? id);
        BuildTechTree();
        Heading("Доступно для изучения (дерево бесконечно)");
        foreach (var a in me.Available.OrderBy(a => a.Cost))
        {
            var t = Rules.Tech(a.Id);
            if (t == null) continue;
            string id = a.Id;
            int left = a.Cost - (a.Id == me.Research ? me.ResearchProgress : Math.Min(me.ResearchProgress, a.Cost));
            int order = Array.IndexOf(me.ResearchPlan, id) + 1;
            var b = Ui.Btn($"{(order > 0 ? $"{order}. " : "")}{t.Name} [{Rules.EraName(t.Era)}] — {a.Cost} ({Turns(left, me.SciRate)})",
                () => Net.SendCommand(CmdType.SetResearch, (int)ProgressionOrder.ToggleQueue, s: id));
            b.Alignment = HorizontalAlignment.Left;
            b.ThemeTypeVariation = a.Id == me.Research ? "TabOn" : "Flat";
            b.CustomMinimumSize = new Vector2(430, 0);
            var info = Ui.V(0, Ui.Lbl("Открывает: " + Unlocks(t), 12, Ui.Dim));
            if (a.Cost < a.BaseCost)
                info.AddChild(Ui.Lbl($"Эффект низкой базы: −{100 - a.Cost * 100 / a.BaseCost}% (обычная цена {a.BaseCost})", 12, Ui.Good));
            Add(Ui.H(12, b, info));
        }
    }

    private void BuildTechTree()
    {
        Heading("Дерево технологий");
        Note("Щёлкните по технологии, чтобы добавить её и её предпосылки в очередь. Повторный щелчок уберёт её вместе с зависимыми шагами. Цифры показывают порядок, ▼ — скидку догоняющего.");
        var graph = TechGraph.ForScience(W.Me, id => Net.SendCommand(CmdType.SetResearch, (int)ProgressionOrder.ToggleQueue, s: id));
        Add(graph);
        if (_scrollToFrontier)
        {
            // on opening, jump to the part of the tree the player is working on
            _scrollToFrontier = false;
            _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, (int)graph.FrontierX);
        }
    }

    private void BuildCivics()
    {
        var me = W.Me;
        _windowTitle.Text = "Социальные институты";
        var cur = Rules.Civic(me.Civic);
        Add(Ui.Lbl(cur == null ? "Выберите институт, чтобы потратить накопленную культуру." : $"Сейчас принимается: {cur.Name} ({me.CivicProgress}/{cur.Cost})",
            15, cur == null ? Ui.Bad : Ui.Gold));
        Add(Ui.Lbl($"Культура империи: +{me.CultureRate} за ход (её дают города: монументы, храмы, театры). Принято институтов: {me.Civics.Length + me.CivicLevel}.", 13));
        var total = Rules.CivicBonus(me.Civics, me.CivicLevel).Describe();
        Add(Ui.Lbl("Действующие бонусы: " + (total.Length > 0 ? total : "пока нет"), 13, Ui.Good));
        Note("Щёлкните по институту, чтобы добавить его и его предпосылки в очередь. Повторный щелчок уберёт его вместе с зависимыми шагами.");
        BuildProgressionPlan(me.CivicPlan, CmdType.SetCivic, id => Rules.Civic(id)?.Name ?? id);
        var graph = TechGraph.ForCivics(me, id => Net.SendCommand(CmdType.SetCivic, (int)ProgressionOrder.ToggleQueue, s: id));
        graph.Accent = Ui.Culture;
        Add(graph);
        if (_scrollToFrontier)
        {
            _scrollToFrontier = false;
            _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, (int)graph.FrontierX);
        }
        Heading("Доступно сейчас");
        foreach (var c in Rules.AvailableCivics(me.Civics, me.CivicLevel).OrderBy(c => c.Cost))
        {
            string id = c.Id;
            int cost = me.CivicCosts.FirstOrDefault(x => x.Id == id)?.Cost ?? c.Cost;
            int order = Array.IndexOf(me.CivicPlan, id) + 1;
            var b = Ui.Btn($"{(order > 0 ? $"{order}. " : "")}{c.Name} — {cost} ({Turns(cost - Math.Min(me.CivicProgress, cost), me.CultureRate)})",
                () => Net.SendCommand(CmdType.SetCivic, (int)ProgressionOrder.ToggleQueue, s: id));
            b.Alignment = HorizontalAlignment.Left;
            b.ThemeTypeVariation = id == me.Civic ? "TabOn" : "Flat";
            b.CustomMinimumSize = new Vector2(380, 0);
            var info = Ui.V(0, Ui.Lbl(c.Effect.Describe(), 12, Ui.Dim));
            if (c.Id == Rules.ReligionCivic) info.AddChild(Ui.Lbl("Открывает религию и шаманство", 12, Ui.Faith));
            if (cost < c.Cost) info.AddChild(Ui.Lbl($"Эффект низкой базы: −{100 - cost * 100 / c.Cost}% (обычная цена {c.Cost})", 12, Ui.Good));
            Add(Ui.H(12, b, info));
        }
    }

    private void BuildProgressionPlan(string[] plan, CmdType command, Func<string, string> name)
    {
        if (plan.Length == 0) return;
        var summary = Ui.V(4, Ui.Lbl($"Очередь · {plan.Length}", 15, Ui.Gold, bold: true));
        summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var labels = plan.Select((id, index) => $"{index + 1}. {name(id)}").ToArray();
        string preview = string.Join(" → ", labels.Take(4));
        if (labels.Length > 4) preview += $" → ещё {labels.Length - 4}";
        var steps = Ui.Lbl(preview, 13, Ui.Dim);
        steps.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        steps.MouseFilter = MouseFilterEnum.Pass;
        steps.TooltipText = string.Join("\n", labels);
        summary.AddChild(steps);
        _progressionSummary.AddChild(Ui.Card(Ui.H(10, summary, Ui.Btn("Очистить",
            () => Net.SendCommand(command, (int)ProgressionOrder.ClearQueue),
            "Очистить очередь. Накопленные очки сохранятся."))));
        _progressionSummary.Visible = true;
    }

    private int _branchTab;

    private void BuildBranches()
    {
        _windowTitle.Text = "Ветки развития";
        var branches = Enumerable.Range(0, Rules.Branches.Count).Where(ShowBranch).ToArray();
        if (branches.Length == 0)
        {
            Add(Ui.Lbl("В этой игре нет особых веток. Хост может добавить их файлами в папке модов (кнопка «Папка модов» в главном меню).", 14, Ui.Dim));
            return;
        }
        if (!branches.Contains(_branchTab)) _branchTab = branches[0];
        var tabs = Ui.Flow(6);
        foreach (int i in branches)
        {
            int idx = i;
            var b = Rules.Branches[i];
            int done = b.Nodes.Count(n => W.Me.Nodes.Contains(n.Id));
            var tb = Ui.Btn($"{b.Name} · {done}/{b.Nodes.Length}{(BranchCanPick(i) ? " · выберите узел" : "")}",
                () => { _branchTab = idx; _windowScroll.ScrollHorizontal = 0; RebuildWindow(); }, b.Description, 14, i == _branchTab ? "TabOn" : null);
            if (i != _branchTab) tb.AddThemeColorOverride("font_color", PowerColor(i));
            tabs.AddChild(tb);
        }
        Add(tabs);
        BuildBranchDetails(_branchTab);
    }

    private void BuildBranchDetails(int branchIndex)
    {
        var me = W.Me;
        var br = Rules.Branches[branchIndex];
        var color = PowerColor(branchIndex);
        float w = Math.Min(_wrapW, 900);
        int rate = branchIndex < me.BranchRate.Length ? me.BranchRate[branchIndex] : 0;
        int progress = branchIndex < me.BranchProgress.Length ? me.BranchProgress[branchIndex] : 0;
        var cur = Rules.Node(branchIndex < me.BranchCurrent.Length ? me.BranchCurrent[branchIndex] : "");
        int pool = branchIndex < me.Power.Length ? me.Power[branchIndex] : 0, poolRate = branchIndex < me.PowerRate.Length ? me.PowerRate[branchIndex] : 0;
        if (br.Description.Length > 0) Note(br.Description);

        var summary = Ui.V(7);
        summary.AddChild(Ui.Flow(22,
            new Chip(Ico.Power, color, $"{br.PowerName} — ресурс ветки для её действий; его дают узлы и здания ветки и ресурсы карты. Не больше {Rules.PowerCap}")
                .Set($"{br.PowerName}: {pool}/{Rules.PowerCap}", $"+{poolRate} за ход"),
            new Chip(Ico.Branch, color, "Очки развития ветки. Ветки идут параллельно с наукой и институтами и ничего у них не отнимают")
                .Set($"+{rate} очк. за ход", $"от: {br.Fuel()}")));
        if (cur != null)
        {
            int cost = me.NodeCosts.FirstOrDefault(c => c.Id == cur.Id)?.Cost ?? cur.Cost;
            summary.AddChild(Ui.H(8, Ui.Lbl($"Осваивается: {cur.Name}", 14, null, true), Ui.Spacer(), Ui.Lbl($"{progress}/{cost} · {Turns(cost - progress, rate)}", 12, Ui.Dim)));
            summary.AddChild(new Bar(color, 6).Set(progress / (float)Math.Max(1, cost), rate / (float)Math.Max(1, cost)));
        }
        else summary.AddChild(Ui.Lbl(BranchCanPick(branchIndex) ? $"Узел не выбран — очки копятся ({progress}). Нажмите на светлый узел ниже." : $"Доступных узлов сейчас нет — очки копятся ({progress}).",
            13, BranchCanPick(branchIndex) ? Ui.Col(0xFFB3AC) : Ui.Dim));
        var total = Rules.NodeBonus(br.Nodes.Where(n => me.Nodes.Contains(n.Id)).Select(n => n.Id)).Describe();
        if (total.Length > 0) summary.AddChild(Ui.Wrap("Уже даёт: " + total, w - 30, 13, Ui.Good));
        if (br.PowerResources.Length > 0)
        {
            var have = br.PowerResources.Select(Rules.ParseResource).Where(r => r != Resource.None).ToList();
            summary.AddChild(Ui.Wrap("Ресурсы карты, питающие ветку (+1 за каждый подключённый): " + string.Join(", ", have.Select(r => (HasRes(r) ? "✔ " : "") + Rules.Res(r).Name)), w - 30, 12, Ui.Dim));
        }
        var card = Ui.Card(summary, 10);
        card.CustomMinimumSize = new Vector2(w, 0);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Add(card);

        var graph = TechGraph.ForBranch(me, branchIndex, id => Net.SendCommand(CmdType.SetNode, s: id));
        graph.Accent = color;
        Add(graph);
        Note("Серый узел с подписью «нужно: …» ждёт технологию, институт или узел другой ветки. Наведите курсор, чтобы увидеть все требования и бонус.");

        if (br.Actions.Length > 0)
        {
            Heading("Действия");
            Note("Действия империи запускаются отсюда. Заклинания юнитов (✦) творит сам юнит: выберите его на карте — кнопки появятся в его панели.");
            foreach (var a in br.Actions)
            {
                var act = a;
                int idx = Rules.Actions.ToList().FindIndex(x => x.Id == a.Id);
                int wait = idx >= 0 && idx < me.Cooldowns.Length ? me.Cooldowns[idx] : 0;
                bool open = W.Knows(a.Requires);
                bool byUnit = a.Caster.Length > 0;
                string casterName = byUnit ? Rules.Units.FirstOrDefault(x => x.Id == a.Caster)?.Name ?? a.Caster : "";
                var cast = Ui.Btn($"{(byUnit ? "✦ " : "")}{a.Name} · {a.Price(br.PowerName)}", () => BeginAction(act, 0), a.Description, 13);
                cast.CustomMinimumSize = new Vector2(300, 0);
                cast.Alignment = HorizontalAlignment.Left;
                cast.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                cast.Disabled = byUnit || !open || wait > 0 || pool < a.CostPower || me.Faith < a.CostFaith || me.Gold < a.CostGold;
                string state = !open ? $"нужно: {Rules.RequirementName(a.Requires)}" : byUnit ? $"творит юнит «{casterName}»"
                    : wait > 0 ? $"перезарядка: {wait} х." : a.Target == "City" ? "после нажатия выберите свой город" : $"повтор через {a.Cooldown} х.";
                var row = Ui.Card(Ui.H(12, cast, Ui.V(1, Ui.Wrap(a.Effect(), w - 350, 13), Ui.Lbl(state, 12, !open ? Ui.Dim : wait > 0 ? Ui.Gold : Ui.Good))), 7);
                row.CustomMinimumSize = new Vector2(w, 0);
                row.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                Add(row);
            }
        }
        if (br.Units.Length + br.Buildings.Length > 0)
        {
            Heading("Юниты и здания ветки (строятся в городах)");
            foreach (var u in br.Units)
            {
                bool known = W.Knows(u.Requires);
                Add(Ui.H(8, new Icon(known ? Ico.Check : Ico.Close, known ? Ui.Good : new Color(Ui.Dim, 0.5f), 13),
                    Ui.Wrap($"Юнит «{u.Name}» — сила {u.Str}, ходы {u.Moves}" + (u.CityDefPct > 0 ? $", +{u.CityDefPct}% в обороне города" : "")
                            + $", цена {u.Cost}. Открывает: {Rules.RequirementName(u.Requires)}" + (u.Abilities().Length > 0 ? $". {u.Abilities()}" : ""), w - 30, 13, known ? Ui.Text : Ui.Dim)));
            }
            foreach (var m in br.Buildings)
            {
                bool known = W.Knows(m.Requires);
                Add(Ui.H(8, new Icon(known ? Ico.Check : Ico.Close, known ? Ui.Good : new Color(Ui.Dim, 0.5f), 13),
                    Ui.Wrap($"Здание «{m.Name}» — {Rules.Building(m.Id)?.Describe()}, цена {m.Cost}. Открывает: {Rules.RequirementName(m.Requires)}", w - 30, 13, known ? Ui.Text : Ui.Dim)));
            }
        }
    }

    private int _relName, _relBelief;
    private bool _showShamanism;

    private bool BranchUnlocked(int index) => Rules.BranchUnlocked(Rules.Branches[index], W.Knows(Rules.ReligionCivic),
        W.Me.Nodes, index < W.Me.BranchCurrent.Length ? W.Me.BranchCurrent[index] : null);

    private bool ShowBranch(int index)
    {
        var branch = Rules.Branches[index];
        return branch.Id != Rules.ShamanismBranch && BranchUnlocked(index)
            && (branch.Nodes.Any(node => W.Me.Nodes.Contains(node.Id) || node.Requires.All(W.Knows))
                || (index < W.Me.BranchCurrent.Length && W.Me.BranchCurrent[index].Length > 0));
    }

    private bool HasBranchAdvisor => Enumerable.Range(0, Rules.Branches.Count).Any(ShowBranch);

    private void OpenBranch(int index)
    {
        if (Rules.Branches[index].Id == Rules.ShamanismBranch)
        {
            _showShamanism = true;
            Toggle(Win.Religion, true);
        }
        else
        {
            _branchTab = index;
            Toggle(Win.Branches, true);
        }
    }

    private void BuildReligion()
    {
        var me = W.Me;
        _windowTitle.Text = "Религия";
        bool knowsRites = W.Knows(Rules.ReligionCivic);
        int shamanism = Enumerable.Range(0, Rules.Branches.Count).FirstOrDefault(i => Rules.Branches[i].Id == Rules.ShamanismBranch, -1);
        bool shamanismOpen = shamanism >= 0 && BranchUnlocked(shamanism);
        if (shamanismOpen)
        {
            Add(Ui.Flow(6,
                Ui.Btn("Вера и религии", () => { _showShamanism = false; _windowScroll.ScrollHorizontal = 0; RebuildWindow(); },
                    variation: !_showShamanism ? "TabOn" : null),
                Ui.Btn("Шаманство", () => { _showShamanism = true; _windowScroll.ScrollHorizontal = 0; RebuildWindow(); },
                    variation: _showShamanism ? "TabOn" : null)));
            if (_showShamanism) { BuildBranchDetails(shamanism); return; }
        }
        if (!knowsRites && me.MyReligion == 0)
        {
            Note("Примите институт «Обряды предков», чтобы основать религию, нанимать миссионеров и изучать шаманство.");
            Add(Ui.Btn("Открыть культуру", () => Toggle(Win.Civics, true)));
            return;
        }
        Add(Ui.Lbl($"Вера: {me.Faith} ({Ui.Signed(me.FaithRate)} за ход). Её дают дворец, святилища, храмы и соборы; институты и реформация умножают её.", 13));
        var mine = me.Religions.FirstOrDefault(r => r.Id == me.MyReligion);
        if (mine != null)
        {
            Add(Ui.Lbl($"Вы основали религию «{mine.Name}». Верование: {Rules.Beliefs[mine.Belief].Name} — {Rules.Beliefs[mine.Belief].Effect.Describe()}", 15, Ui.Gold));
            Add(Ui.Lbl($"Ей следуют городов: {mine.Cities}. Каждый такой город даёт вам +1 веры, чужой — ещё и +1 золота за ход.", 13));
        }
        else if (me.Religions.Length >= Rules.Beliefs.Length) Add(Ui.Lbl("Все верования уже заняты — основать новую религию нельзя.", 14, Ui.Dim));
        else
        {
            int price = Rules.ReligionPrice(me.Religions.Length);
            Heading($"Основать религию — {price} веры");
            var freeNames = Enumerable.Range(0, Rules.ReligionNames.Length).Where(i => me.Religions.All(r => r.Name != Rules.ReligionNames[i])).ToList();
            var freeBeliefs = Enumerable.Range(0, Rules.Beliefs.Length).Where(i => me.Religions.All(r => r.Belief != i)).ToList();
            if (!freeNames.Contains(_relName)) _relName = freeNames[0];
            if (!freeBeliefs.Contains(_relBelief)) _relBelief = freeBeliefs[0];
            var names = Ui.Flow(5);
            foreach (int i in freeNames)
            {
                int idx = i;
                names.AddChild(Ui.Btn(Rules.ReligionNames[i], () => { _relName = idx; RebuildWindow(); }, size: 13, variation: i == _relName ? "TabOn" : null));
            }
            Add(Ui.Lbl("Название:", 13, Ui.Dim)); Add(names);
            Add(Ui.Lbl("Верование (действует в каждом городе, который следует религии, чей бы он ни был):", 13, Ui.Dim));
            foreach (int i in freeBeliefs)
            {
                int idx = i;
                var bb = Ui.Btn($"{Rules.Beliefs[i].Name} — {Rules.Beliefs[i].Effect.Describe()}", () => { _relBelief = idx; RebuildWindow(); }, size: 13, variation: i == _relBelief ? "TabOn" : "Flat");
                bb.Alignment = HorizontalAlignment.Left;
                Add(bb);
            }
            var found = Ui.Btn($"Основать «{Rules.ReligionNames[_relName]}» со священным городом в столице", () => Net.SendCommand(CmdType.FoundReligion, _relName, _relBelief), size: 15, variation: "Primary");
            found.Disabled = me.Faith < price;
            Add(found);
        }

        Heading("Религии мира");
        if (me.Religions.Length == 0) Add(Ui.Lbl("Пока не основано ни одной религии.", 13, Ui.Dim));
        foreach (var r in me.Religions)
            Add(Ui.Lbl($"«{r.Name}» — основатель: {W.PlayerName(r.Founder)}; {Rules.Beliefs[r.Belief].Name} ({Rules.Beliefs[r.Belief].Effect.Describe()}); городов: {r.Cities}", 14,
                r.Id == me.MyReligion ? Ui.Gold : Colors.White));

        Heading("Ваши города");
        Add(Ui.Lbl($"Религия сама расходится на соседние города (до {Rules.ReligionRange} клеток); священный город влияет сильнее. Миссионер ({Rules.MissionaryFaith} веры) разом добавляет городу {Rules.PreachPressure} давления и проходит через любые границы.", 12, Ui.Dim));
        foreach (var c in W.Cities.Values.Where(c => c.Owner == W.MyId).OrderBy(c => c.Id))
        {
            int cid = c.Id;
            var rel = me.Religions.FirstOrDefault(r => r.Id == c.Religion);
            var name = Ui.Lbl($"{c.Name}: {(rel == null ? "без религии" : rel.Name)}", 14);
            name.CustomMinimumSize = new Vector2(300, 0);
            var recruit = Ui.Btn($"Нанять миссионера ({Rules.MissionaryFaith} веры)", () => Net.SendCommand(CmdType.Recruit, cid), size: 13);
            bool isFull = !W.HasUnitSpace(W.MyId, c.Tile, Rules.UnitIndex("missionary"));
            recruit.Disabled = isFull || me.Faith < Rules.MissionaryFaith || (mine == null && rel == null);
            if (isFull) recruit.TooltipText = "На клетке города нет места для ещё одного мирного юнита";
            Add(Ui.H(10, name, recruit));
        }
    }

    private void BuildEconomy()
    {
        var me = W.Me;
        _windowTitle.Text = "Экономика";
        Heading("Казна");
        var split = new Bar(Ui.Science, 10, 240).WithBack(Ui.Col(0x9A8440)).Set(me.Slider / 100f);
        split.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Add(Ui.H(10,
            Ui.Btn("−10%", () => Net.SendCommand(CmdType.SetSlider, Math.Max(0, me.Slider - 10)), "Меньше науки, больше золота", 13),
            new Chip(Ico.Science, Ui.Science).Set($"Наука {me.Slider}%"), split, new Chip(Ico.Gold, Ui.Gold).Set($"Золото {100 - me.Slider}%"),
            Ui.Btn("+10%", () => Net.SendCommand(CmdType.SetSlider, Math.Min(100, me.Slider + 10)), "Больше науки, меньше золота", 13),
            Ui.Lbl("— так делится коммерция ваших городов", 12, Ui.Dim)));
        GridContainer Table()
        {
            var g = new GridContainer { Columns = 2 };
            g.AddThemeConstantOverride("h_separation", 28);
            g.AddThemeConstantOverride("v_separation", 3);
            return g;
        }
        var grid = Table();
        void Line(GridContainer g, string name, string value, Color? col = null, bool bold = false) { g.AddChild(Ui.Lbl(name, 13, Ui.Dim)); g.AddChild(Ui.Lbl(value, 13, col, bold)); }
        Line(grid, "Доход городов", $"+{me.Income}", Ui.Good);
        Line(grid, "Содержание юнитов", Cost(me.UnitUpkeep));
        Line(grid, "Содержание зданий", Cost(me.BuildingUpkeep));
        Line(grid, "Содержание городов (бюрократия)", Cost(me.CityMaint));
        Line(grid, "Содержание дорог", Cost(me.RoadUpkeep));
        Line(grid, "«Логистический надрыв» (штраф лидера)", Cost(me.StrainCost), me.StrainCost > 0 ? Ui.Bad : null);
        Line(grid, "Итого за ход", Ui.Signed(me.GoldRate), me.GoldRate < 0 ? Ui.Bad : Ui.Gold, true);
        var stock = Table();
        Line(stock, "В казне", me.Gold.ToString(), Ui.Gold, true);
        Line(stock, "Вера", $"{me.Faith} ({Ui.Signed(me.FaithRate)} за ход)", Ui.Faith);
        Line(stock, "Одобрение", $"{me.Favor} ({Ui.Signed(me.FavorRate)} за ход)", Ui.Favor);
        Line(stock, "Роскошь", me.Luxuries == 0 ? "нет" : $"{me.Luxuries} вид(ов): +{me.Luxuries} довольства в городах");
        var have = me.Resources.Select(r => Rules.Res((Resource)r)).ToList();
        var resources = Ui.Wrap(have.Count == 0 ? "Подключённых ресурсов нет — рабочие подключают их улучшениями" : "Ресурсы: " + string.Join(", ", have.Select(r => r.Name)), 360, 12, Ui.Dim);
        var left = Ui.Card(Ui.V(6, Ui.Lbl("ЗА ХОД", 11, Ui.Dim, true), grid), 10);
        var right = Ui.Card(Ui.V(6, Ui.Lbl("ЗАПАСЫ", 11, Ui.Dim, true), stock, resources), 10);
        left.SizeFlagsVertical = right.SizeFlagsVertical = SizeFlags.Fill;
        Add(Ui.H(10, left, right));

        Heading("Источники дохода");
        foreach (var group in me.IncomeLines.GroupBy(line => line.Resource))
        {
            string title = group.Key switch
            {
                "gold" => "Золото", "science" => "Наука", "culture" => "Культура", "faith" => "Вера", "favor" => "Одобрение",
                _ => Rules.Branches.FirstOrDefault(b => b.Id == group.Key)?.PowerName ?? group.Key,
            };
            var items = Table();
            foreach (var item in group) Line(items, item.Source, Ui.Signed(item.Delta), item.Delta < 0 ? Ui.Bad : Ui.Good);
            Add(Ui.Card(Ui.V(4, Ui.Lbl(title, 14, Ui.Gold, true), items), 7));
        }

        Heading("Анти-Сноуболл: ваше положение в мире");
        Add(Ui.Lbl($"Индекс технологического разрыва (ITR): {me.Itr:+0.00;−0.00}", 15, null, true));
        if (me.Pressure > 0.005f)
        {
            Add(Ui.Lbl($"Вы оторвались от остального мира. Давление лидера: {me.Pressure * 100:0}%", 14, Ui.Bad));
            Add(Ui.Lbl($"• Информационный вакуум: наука ×{me.SciMult:0.00}, культура ×{me.CulMult:0.00}", 14));
            Add(Ui.Lbl($"• Логистический надрыв: содержание ×{me.UpkeepMult:0.00}", 14));
        }
        else if (me.Lag > 0.005f)
        {
            Add(Ui.Lbl($"Вы отстаёте от мира — действуют бонусы догоняющего ({me.Lag * 100:0}%).", 14, Ui.Good));
            Add(Ui.Lbl("• Эффект низкой базы: технологии, институты и узлы веток, уже открытые другими, стоят дешевле; ваши голоса в конгрессе весят больше", 14));
            Add(Ui.Lbl($"• Научное эхо от мирных контактов с развитыми державами: +{me.EchoBonus} науки за ход (больше при открытых границах)", 14));
        }
        else Add(Ui.Lbl("Вы идёте вровень с миром: ни штрафов лидера, ни бонусов догоняющего.", 14, Ui.Dim));
        if (me.Sprawl > 0.01f)
            Add(Ui.Lbl($"• Раздутая империя: городов заметно больше, чем у соседей — содержание дополнительно растёт (индекс {me.Sprawl:0.00})", 14, Ui.Gold));

        Heading("Связи с миром");
        Add(Ui.Lbl(me.TradeLuxuries > 0 ? $"• Импорт роскоши: +{me.TradeLuxuries} довольства в каждом городе (не больше {Rules.MaxTradeLuxuries})"
            : "• Импорт роскоши можно предложить в разделе «Державы» (F4)", 14, me.TradeLuxuries > 0 ? Ui.Good : Ui.Dim));
        Add(Ui.Lbl(me.WarWeariness > 0 ? $"• Долгих войн: {me.WarWeariness} (порог {Rules.WarWearinessAfter} ходов)"
            : "• Усталости от войны нет", 14, me.WarWeariness > 0 ? Ui.Bad : Ui.Dim));
        var state = me.Religions.FirstOrDefault(r => r.Id == me.StateReligion);
        Add(Ui.Lbl(state == null ? "• Государственной религии нет (ею становится религия столицы)"
            : $"• Государственная религия — «{state.Name}». Единоверцев среди известных держав: {me.Brethren} (сильнее научное эхо, ИИ охотнее идёт на мир и открытые границы)", 14,
            me.Brethren > 0 ? Ui.Good : Ui.Dim));
        Add(Ui.Btn("Демография и рейтинг держав (F9)", () => Toggle(Win.Demographics, true), size: 13));
        Heading("Города");
        var cities = new GridContainer { Columns = 7 };
        cities.AddThemeConstantOverride("h_separation", 20);
        foreach (string h in new[] { "Город", "Нас.", "Пища", "Произв.", "Наука", "Золото", "Строит" }) cities.AddChild(Ui.Lbl(h, 13, Ui.Dim));
        foreach (var c in W.Cities.Values.Where(c => c.Owner == W.MyId && c.Detail != null).OrderBy(c => c.Id))
        {
            int id = c.Id;
            var d = c.Detail!;
            cities.AddChild(Ui.Btn(c.Name, () => OpenCity(id), size: 13));
            cities.AddChild(Ui.Lbl(c.Pop.ToString(), 13));
            cities.AddChild(Ui.Lbl(Ui.Signed(d.FoodRate), 13, d.FoodRate < 0 ? Ui.Bad : null));
            cities.AddChild(Ui.Lbl($"+{d.ProdRate}", 13));
            cities.AddChild(Ui.Lbl($"+{d.Science}", 13, Ui.Science));
            cities.AddChild(Ui.Lbl($"+{d.Gold}", 13, Ui.Gold));
            cities.AddChild(Ui.Lbl(d.Queue.Length > 0 ? Rules.ItemName(d.Queue[0]) : "— простаивает —", 13,
                d.Queue.Length == 0 ? Ui.Bad : null));
        }
        Add(cities);
    }

    private void BuildCongress()
    {
        var me = W.Me;
        Heading("Всемирный конгресс");
        var chairName = me.Chair >= 0 ? Ui.Lbl($"Председатель: «{W.PlayerName(me.Chair)}»", 15, Ui.Gold, true) : Ui.Lbl("Председатель не избран", 15, Ui.Dim, true);
        Add(Ui.H(14, chairName, Ui.Pill($"заседание через {me.CongressIn} х.", Ui.Favor), Ui.Spacer(),
            new Chip(Ico.Favor, Ui.Favor, "Ваше дипломатическое одобрение и его прирост за ход").Set(me.Favor.ToString(), Ui.Signed(me.FavorRate))));
        Note($"Каждое очко одобрения — один голос; голоса отстающих держав весят больше. Раз в {Rules.CongressPeriod} ходов конгресс выбирает председателя (+10% золота и культуры) и решает судьбу резолюции. Одобрение дают мир, единоверцы и мировые достижения.");
        var voteRow = Ui.Flow(6);
        voteRow.AddChild(Ui.Lbl("В председатели:", 13, Ui.Dim));
        foreach (var cand in W.Players.Values.Where(x => x.Alive && !x.Neutral && (x.Met || x.Id == W.MyId)).OrderBy(x => x.Id))
        {
            int cid = cand.Id;
            int votes = cid < me.Votes.Length ? me.Votes[cid] : 0;
            var vb = Ui.Btn($"{cand.Name} · {votes} гол.", () => Net.SendCommand(CmdType.Vote, cid, 10), "Отдать 10 одобрения за этого кандидата", 13);
            vb.AddThemeColorOverride("font_color", Ui.Col(cand.Color).Lightened(0.35f));
            vb.Disabled = me.Favor < 10;
            voteRow.AddChild(vb);
        }
        Add(voteRow);

        var active = Rules.Resolution(me.Resolution);
        var proposed = Rules.Resolution(me.Proposed);
        var res = Ui.V(6);
        res.AddChild(active != null
            ? Ui.H(8, Ui.Pill("действует", Ui.Good), Ui.Lbl($"«{active.Name}»: {active.Effect}", 13))
            : Ui.H(8, Ui.Pill("действующей резолюции нет", Ui.Dim)));
        if (proposed != null)
        {
            int total = me.VotesFor + me.VotesAgainst;
            var yes = Ui.Btn("За · 10 одобр.", () => Net.SendCommand(CmdType.VoteResolution, 1, 10), "Резолюция принимается, если голосов «за» больше, чем «против»", 13);
            var no = Ui.Btn("Против · 10 одобр.", () => Net.SendCommand(CmdType.VoteResolution, 0, 10), size: 13);
            yes.Disabled = no.Disabled = me.Favor < 10;
            res.AddChild(Ui.H(8, Ui.Pill("на голосовании", Ui.Gold), Ui.Lbl($"«{proposed.Name}»: {proposed.Effect}", 13)));
            var tally = new Bar(Ui.Good, 8, 220).WithBack(total > 0 ? Ui.Col(0x8A3B40) : Ui.Col(0x0A0E18)).Set(total > 0 ? me.VotesFor / (float)total : 0);
            tally.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            res.AddChild(Ui.H(10, Ui.Lbl($"за {me.VotesFor}", 13, Ui.Good), tally, Ui.Lbl($"против {me.VotesAgainst}", 13, Ui.Bad), Ui.Spacer(), yes, no));
        }
        Add(Ui.Card(res, 10));
    }

    private void BuildDemographics()
    {
        _windowTitle.Text = "Рейтинг держав и мировые достижения";
        var me = W.Me;
        Heading("Мировые достижения");
        Note($"У бесконечной партии нет финала, но есть за что соревноваться: первая держава, достигшая цели, получает +{Rules.MilestoneFavor} дипломатического одобрения. Каждое достижение достаётся только одному.");
        var miles = new GridContainer { Columns = 2 };
        miles.AddThemeConstantOverride("h_separation", 10);
        miles.AddThemeConstantOverride("v_separation", 5);
        for (int i = 0; i < Rules.Milestones.Length; i++)
        {
            int who = i < me.Milestones.Length ? me.Milestones[i] : -1;
            bool mine = who == W.MyId;
            var holder = who < 0 ? Ui.Lbl("свободно", 12, Ui.Dim) : Ui.Lbl(mine ? "ваше" : W.PlayerName(who), 12, mine ? Ui.Good : W.Players.TryGetValue(who, out var hp) && hp.Met ? Map.OwnerColor(who).Lightened(0.3f) : Ui.Dim);
            var row = Ui.H(8, new Icon(who < 0 ? Ico.Star : Ico.Check, who < 0 ? new Color(Ui.Dim, 0.5f) : mine ? Ui.Good : Ui.Gold, 15),
                Ui.Lbl(Rules.Milestones[i].Name, 13, who < 0 ? Ui.Text : mine ? Ui.Good : Ui.Dim), Ui.Spacer(), holder);
            var card = Ui.Card(row, 7, mine ? Ui.Col(0x16301F, 0.9f) : null);
            card.CustomMinimumSize = new Vector2(Math.Min(440, (_wrapW - 20) / 2), 0);
            miles.AddChild(card);
        }
        Add(miles);

        Heading("Демография");
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 34);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (string h in new[] { "Показатель", "Вы", "Место", "Лучший", "Средний", "Худший" }) grid.AddChild(Ui.Lbl(h.ToUpperInvariant(), 11, Ui.Dim, true));
        foreach (var d in me.Demo)
        {
            grid.AddChild(Ui.Lbl(d.Key, 14));
            grid.AddChild(Ui.Lbl(d.Mine.ToString(), 14, Ui.Gold, true));
            grid.AddChild(Ui.Lbl(d.Peers > 0 ? d.Rank.ToString() : "—", 14, d.Peers > 0 && d.Rank == 1 ? Ui.Good : null));
            grid.AddChild(Ui.Lbl(d.Peers > 0 ? d.Best.ToString() : "—", 14));
            grid.AddChild(Ui.Lbl(d.Peers > 0 ? d.Avg.ToString() : "—", 14));
            grid.AddChild(Ui.Lbl(d.Peers > 0 ? d.Worst.ToString() : "—", 14));
        }
        Add(grid);

        Heading("Известные цивилизации");
        var known = W.Players.Values.Where(p => !p.Neutral && (p.Met || p.Id == W.MyId)).OrderByDescending(p => p.Score).ToList();
        int best = Math.Max(1, known.Where(p => p.Id == W.MyId || Shows(p, StatAccess.Territory)).Select(p => p.Score).DefaultIfEmpty(1).Max());
        foreach (var p in known)
        {
            var name = Ui.Lbl(p.Name + (p.Id == W.MyId ? " (вы)" : ""), 14, Ui.Col(p.Color).Lightened(0.3f), true);
            name.CustomMinimumSize = new Vector2(220, 0);
            var row = Ui.H(12, name);
            bool territory = p.Id == W.MyId || Shows(p, StatAccess.Territory);
            if (territory)
            {
                var bar = new Bar(Ui.Col(p.Color), 8, 190).Set(p.Score / (float)best);
                bar.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                row.AddChild(bar);
                row.AddChild(Ui.Lbl($"{p.Score} оч. · городов {p.Cities}", 13));
            }
            if (p.Id == W.MyId || Shows(p, StatAccess.Development))
                row.AddChild(Ui.Lbl(Rules.EraName(p.Era), 12, Ui.Dim));
            if (!territory) row.AddChild(Ui.Lbl("Счёт закрыт", 12, Ui.Dim));
            if (!p.Alive) row.AddChild(Ui.Lbl("Устранена", 12, Ui.Dim));
            Add(row);
        }
    }

    private void BuildHelp()
    {
        _windowTitle.Text = "Справка";
        Heading("Управление");
        var keys = new GridContainer { Columns = 4 };
        keys.AddThemeConstantOverride("h_separation", 14);
        keys.AddThemeConstantOverride("v_separation", 5);
        void Key(string key, string what)
        {
            var k = Ui.Pill(key, Ui.Gold, 12);
            k.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            keys.AddChild(k);
            keys.AddChild(Ui.Wrap(what, Math.Min(360, (_wrapW - 420) / 2), 13));
        }
        Key("ЛКМ", "выбрать юнит; ещё раз — следующий на клетке; город — его панель");
        Key("F12", "диагностика, FPS, сеть и выбранная клетка");
        Key("ПКМ", "идти или атаковать; при открытом городе — купить клетку");
        Key("Колесо", "масштаб карты");
        Key("WASD / перетаскивание", "движение камеры");
        Key("Q / E, Shift + мышь", "поворот камеры");
        Key("Enter", "завершить ход");
        Key("N", "следующий юнит без приказа");
        Key("Пробел", "пропустить юнит до следующего хода");
        Key("F", "укрепиться / спать");
        Key("B", "основать город (поселенец)");
        Key("P", "проповедовать (миссионер в городе)");
        Key("C", "камера на выбранный юнит");
        Key("T", "чат");
        Key("M", "звук вкл/выкл");
        Key("Esc", "закрыть окно, отменить заклинание, снять выбор");
        foreach (var a in Advisors) Key(a.key, a.title + " — " + a.tip.ToLower());
        Add(keys);

        Heading("Как устроена игра");
        void Topic(Ico icon, Color color, string title, string text)
            => Add(Ui.Card(Ui.H(10, new Icon(icon, color, 20), Ui.V(2, Ui.Lbl(title, 14, color, true), Ui.Wrap(text, Math.Min(_wrapW, 900) - 70, 13))), 9));
        Topic(Ico.City, Ui.Prod, "Города", "Жители обрабатывают клетки вокруг города: пища растит население, производство строит, коммерция делится на науку и золото. Рабочие улучшают клетки и подключают ресурсы: бонусные усиливают клетку, стратегические нужны для юнитов, роскошь поднимает довольство.");
        Topic(Ico.Sword, Ui.Good, "Маршруты и разведка", "Наведите курсор на разведанную клетку, чтобы увидеть маршрут и ход прибытия. Число 1 означает текущий ход. Щёлкните ПКМ, чтобы отдать приказ. Авторазведка ищет доступные края тумана, а север и юг карты соединены так же, как запад и восток.");
        Topic(Ico.Science, Ui.Science, "Наука и культура", $"Выберите любую цель в дереве. Игра сама изучит её предпосылки по порядку. Деревья подпитывают друг друга: каждая технология добавляет {Rules.Inspiration * 100:0}% своей цены к текущему институту, и наоборот.");
        Topic(Ico.Branch, Ui.Power, "Ветки развития", "Магия и ветки из модов растут параллельно. Узлы дают бонусы, юнитов, здания и действия. Обряды предков открывают шаманство во вкладке «Религия» (F8).");
        Topic(Ico.Faith, Ui.Faith, "Религия", "Вера покупает мирные проекты и основывает религию. Верование действует в каждом городе, который ей следует; религия расходится сама и с миссионерами. Единая вера успокаивает города и сближает державы.");
        Topic(Ico.Favor, Ui.Favor, "Державы и конгресс", "В разделе «Державы» (F4) заключают сделки, открывают границы, создают оборонительные союзы и делятся показателями. Одобрение можно обменять, потратить на разведку или отдать как голоса в конгрессе.");
        Topic(Ico.Sword, Ui.Bad, "Война", "Перед атакой наведите курсор на врага: панель покажет шанс победы и урон. Холмы, лес, укрепление и стены помогают обороне. Долгая война утомляет ваши города.");
        Topic(Ico.Chart, Ui.Good, "Анти-Сноуболл", "Игра сдерживает отрыв: у лидера дороже содержание и медленнее развитие, а отстающие получают скидки на открытые миром технологии и научное эхо. Положение показано в верхней панели.");
    }
}
