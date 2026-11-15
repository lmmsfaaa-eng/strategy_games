using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;

namespace IH.Client;

public partial class GameScreen
{
    private enum DiplomacyTab { Nations, Inbox, Intelligence, Congress }
    private DiplomacyTab _diplomacyTab;
    private int _nation = -1;
    private readonly VBoxContainer _nationHud = Ui.V(3);
    private ScrollContainer _nationScroll = null!;
    private string _nationHudState = "";

    private IEnumerable<PlayerDto> Contacts => W.Players.Values
        .Where(p => p.Id != W.MyId && p.Alive && !p.Neutral && p.Met).OrderBy(p => p.Id);

    private int InboxCount => W.Me.Proposals.Count(p => p.To == W.MyId) + W.Me.DefenseCalls.Length;

    private void OpenDiplomacy(DiplomacyTab tab, int nation = -1)
    {
        _diplomacyTab = tab;
        if (nation >= 0) _nation = nation;
        Toggle(Win.Diplomacy, true);
    }

    private void BuildNationHud(VBoxContainer box)
    {
        _nationScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
        _nationScroll.AddChild(_nationHud);
        _nationHud.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(_nationScroll);
    }

    private void RefreshNationHud()
    {
        var shared = Contacts.Where(p => p.SharedStats != 0).ToArray();
        int incoming = InboxCount;
        string state = incoming + "|" + string.Join("|", shared.Select(p =>
            $"{p.Id}:{p.Name}:{p.SharedStats}:{string.Join(";", SharedMetricLines(p, false))}"));
        if (state == _nationHudState) return;
        _nationHudState = state;
        Ui.Clear(_nationHud);
        _nationScroll.Visible = shared.Length > 0 || incoming > 0;
        if (!_nationScroll.Visible) return;
        _nationScroll.CustomMinimumSize = new Vector2(290, Math.Min(178, shared.Length * 45 + (incoming > 0 ? 32 : 0)));
        if (incoming > 0) _nationHud.AddChild(Ui.Btn($"Входящие · {incoming}", () => OpenDiplomacy(DiplomacyTab.Inbox),
            "Предложения и призывы к защите", 13, "Primary"));
        foreach (var p in shared)
        {
            var name = Ui.Lbl(p.Name, 13, Ui.Col(p.Color).Lightened(0.35f), true);
            var stats = Ui.Lbl(string.Join("  ·  ", SharedMetricLines(p, true)), 11, Ui.Dim);
            stats.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            stats.TooltipText = string.Join("\n", SharedMetricLines(p, false));
            _nationHud.AddChild(Ui.Clickable(Ui.V(1, name, stats), () => OpenDiplomacy(DiplomacyTab.Nations, p.Id), stats.TooltipText, margin: 4));
        }
    }

    private static bool Shows(PlayerDto player, StatAccess category) => ((StatAccess)player.SharedStats & category) != 0;

    private static IEnumerable<string> SharedMetricLines(PlayerDto p, bool shortForm)
    {
        if (Shows(p, StatAccess.Economy)) yield return shortForm ? $"{p.Gold} зол. ({Ui.Signed(p.GoldRate)})" : $"Казна {p.Gold} · {Ui.Signed(p.GoldRate)} золота за ход";
        if (Shows(p, StatAccess.Development)) yield return shortForm ? $"{Rules.EraName(p.Era)} · {p.Techs} техн." : $"{Rules.EraName(p.Era)} · технологий {p.Techs}";
        if (Shows(p, StatAccess.Territory)) yield return shortForm ? $"{p.Cities} гор. · {p.Score} оч." : $"Городов {p.Cities} · общий счёт {p.Score}";
    }

    private void BuildDiplomacy()
    {
        _windowTitle.Text = "Державы";
        var tabs = Ui.Flow(6);
        foreach (var (tab, label) in new[]
        {
            (DiplomacyTab.Nations, "Державы"),
            (DiplomacyTab.Inbox, InboxCount > 0 ? $"Входящие · {InboxCount}" : "Входящие"),
            (DiplomacyTab.Intelligence, "Разведка"),
            (DiplomacyTab.Congress, "Конгресс"),
        })
            tabs.AddChild(Ui.Btn(label, () => OpenDiplomacy(tab), variation: tab == _diplomacyTab ? "TabOn" : "Tab"));
        Add(tabs);
        switch (_diplomacyTab)
        {
            case DiplomacyTab.Nations: BuildNationDetails(); break;
            case DiplomacyTab.Inbox: BuildDiplomacyInbox(); break;
            case DiplomacyTab.Intelligence: BuildIntelligence(); break;
            case DiplomacyTab.Congress: BuildCongress(); break;
        }
    }

    private void BuildNationDetails()
    {
        var contacts = Contacts.ToArray();
        if (contacts.Length == 0) { Note("Исследуйте карту и установите контакт с соседями, чтобы договариваться с ними."); return; }
        if (contacts.All(p => p.Id != _nation)) _nation = contacts[0].Id;
        var names = Ui.Flow(5);
        foreach (var p in contacts)
            names.AddChild(Ui.Btn(p.Name, () => OpenDiplomacy(DiplomacyTab.Nations, p.Id),
                p.AtWar ? "Война" : p.Allied ? "Оборонительный союз" : "Мир", 13, p.Id == _nation ? "TabOn" : "Tab"));
        Add(names);
        var other = contacts.First(p => p.Id == _nation);
        string relation = other.AtWar ? "Война" : other.Allied ? "Оборонительный союз" : "Мир";
        Add(Ui.H(8, Ui.Lbl(other.Name, 20, Ui.Col(other.Color).Lightened(0.35f), true), Ui.Pill(relation, other.AtWar ? Ui.Bad : Ui.Good),
            Ui.Lbl(other.AI ? "ИИ" : other.Connected ? "Игрок в сети" : "Игрок не в сети", 12, Ui.Dim)));

        Heading("Договоры");
        var imports = W.Me.LuxuryAgreements.Where(x => x.From == other.Id || x.To == other.Id).ToArray();
        var terms = new List<string>();
        if (other.OpenBorders) terms.Add("Открытые границы");
        if (other.Embargo) terms.Add("Ваше эмбарго");
        if (other.Embargoed) terms.Add("Их эмбарго");
        terms.AddRange(imports.Select(x => $"{(x.To == W.MyId ? "Получаем" : "Поставляем")} {Rules.Res((Resource)x.Resource).Name} · до хода {x.Expires}"));
        Note(terms.Count > 0 ? string.Join(" · ", terms) : "Действующих поставок и дополнительных соглашений нет.");

        var actions = Ui.Flow(6);
        var openOffer = Ui.Btn("Предложить сделку", () => ShowTradeComposer(other.Id), "Обмен золотом, одобрением, роскошью и технологиями", variation: "Primary");
        openOffer.Disabled = other.AtWar || other.Embargo || other.Embargoed;
        if (openOffer.Disabled) openOffer.TooltipText = other.AtWar ? "Сначала заключите мир" : "Сделки заблокированы эмбарго";
        actions.AddChild(openOffer);
        int pid = other.Id;
        if (other.AtWar)
            actions.AddChild(Ui.Btn("Предложить мир", () => SendProposal(pid, ProposalKind.Peace)));
        else
        {
            actions.AddChild(other.OpenBorders
                ? Ui.Btn("Закрыть границы", () => Net.SendCommand(CmdType.CancelBorders, pid))
                : Ui.Btn("Открыть границы", () => SendProposal(pid, ProposalKind.OpenBorders)));
            actions.AddChild(other.Allied
                ? Ui.Btn("Расторгнуть союз", () => Net.SendCommand(CmdType.CancelAlliance, pid))
                : Ui.Btn("Предложить союз", () => SendProposal(pid, ProposalKind.Alliance), "Союзник сможет принять призыв о защите"));
            var war = Ui.Btn("Объявить войну", () => ConfirmWar(pid), variation: "Danger");
            war.Disabled = other.Allied;
            if (war.Disabled) war.TooltipText = "Сначала расторгните оборонительный союз";
            actions.AddChild(war);
        }
        actions.AddChild(Ui.Btn(other.Embargo ? "Снять эмбарго" : "Ввести эмбарго",
            () => Net.SendCommand(CmdType.SetEmbargo, pid, other.Embargo ? 0 : 1),
            other.Embargo ? "Снова разрешить сделки со своей стороны" : "Прекратить действующие поставки и заблокировать новые сделки"));
        Add(actions);

        Heading("Данные держав");
        foreach (string metric in SharedMetricLines(other, false)) Add(Ui.Lbl(metric, 14));
        if (other.SharedStats == 0) Note("Эта держава не открыла вам свои показатели. Их владелец управляет доступом.");
        Add(Ui.Lbl($"Ваши данные для державы «{other.Name}»", 14, Ui.Dim));
        var permissions = Ui.Flow(10);
        foreach (var (bit, label) in new[]
        {
            (StatAccess.Economy, "Экономика"), (StatAccess.Development, "Развитие"), (StatAccess.Territory, "Территория"),
        })
        {
            var toggle = new CheckBox { Text = label, ButtonPressed = ((StatAccess)other.SharingStats & bit) != 0 };
            toggle.Toggled += enabled =>
            {
                if (!W.Players.TryGetValue(pid, out var current)) return;
                var mask = (StatAccess)current.SharingStats;
                Net.SendCommand(CmdType.SetStatAccess, pid, (int)(enabled ? mask | bit : mask & ~bit));
                foreach (var control in permissions.GetChildren().OfType<CheckBox>()) control.Disabled = true;
            };
            permissions.AddChild(toggle);
        }
        Add(permissions);
    }

    private void SendProposal(int pid, ProposalKind kind) => Net.SendCommand(CmdType.Propose, pid, (int)kind);

    private void ConfirmWar(int target)
    {
        if (!W.Players.TryGetValue(target, out var nation) || nation.Allied || nation.AtWar) return;
        var ending = new List<string>();
        if (nation.OpenBorders) ending.Add("открытые границы");
        int imports = W.Me.LuxuryAgreements.Count(x => x.From == target || x.To == target);
        if (imports > 0) ending.Add($"поставки роскоши ({imports})");
        int proposals = W.Me.Proposals.Count(p => p.From == target || p.To == target);
        if (proposals > 0) ending.Add($"ожидающие предложения ({proposals})");
        ShowWarDialog($"Объявить войну державе «{nation.Name}»?", ending.Count == 0
                ? "Действующих договоров и поставок между державами нет."
                : $"Прекратятся: {string.Join(", ", ending)}.",
            () => Net.SendCommand(CmdType.DeclareWar, target));
    }

    private void ShowWarDialog(string title, string description, Action confirmed)
    {
        var dialog = new ConfirmationDialog
        {
            Title = "Подтверждение войны", DialogText = $"{title}\n{description}\nОборонительные союзники цели могут вступить в войну.",
            OkButtonText = "Объявить войну",
        };
        AddChild(dialog);
        dialog.GetCancelButton().Text = "Отмена";
        dialog.Confirmed += () => { confirmed(); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    private void BuildDiplomacyInbox()
    {
        if (W.Me.Proposals.Length == 0 && W.Me.DefenseCalls.Length == 0) { Note("Предложений пока нет."); return; }
        foreach (var call in W.Me.DefenseCalls.OrderBy(c => c.Expires))
        {
            Add(Ui.Card(Ui.V(6, Ui.Lbl($"Призыв к защите · {W.PlayerName(call.Ally)}", 16, Ui.Gold, true),
                Ui.Lbl($"Вступить в войну против «{call.AggressorName}» · до хода {call.Expires}", 13),
                Ui.H(6, Ui.Btn("Вступить в войну", () => Net.SendCommand(CmdType.RespondDefense, call.Id, 1), variation: "Danger"),
                    Ui.Btn("Отклонить", () => Net.SendCommand(CmdType.RespondDefense, call.Id, 0))))));
        }
        foreach (var pr in W.Me.Proposals.OrderBy(p => p.To != W.MyId).ThenBy(p => p.Expires))
        {
            bool incoming = pr.To == W.MyId;
            var kind = (ProposalKind)pr.Kind;
            string title = kind switch
            {
                ProposalKind.Peace => "Мир", ProposalKind.OpenBorders => "Открытые границы",
                ProposalKind.Alliance => "Оборонительный союз", _ => "Сделка",
            };
            var card = Ui.V(6, Ui.Lbl($"{title} · {W.PlayerName(incoming ? pr.From : pr.To)}", 16, Ui.Gold, true),
                Ui.Lbl($"{(incoming ? "Входящее" : "Отправлено")} · до хода {pr.Expires}", 12, Ui.Dim));
            if (kind == ProposalKind.Trade)
            {
                card.AddChild(Ui.Wrap("Вы отдаёте · " + DescribeTradeSide(incoming ? pr.Requested : pr.Offered), _wrapW - 26, 13));
                card.AddChild(Ui.Wrap("Вы получаете · " + DescribeTradeSide(incoming ? pr.Offered : pr.Requested), _wrapW - 26, 13));
            }
            card.AddChild(incoming ? Ui.H(6,
                Ui.Btn("Принять", () => Net.SendCommand(CmdType.Respond, pr.Id, 1), variation: "Primary"),
                Ui.Btn("Отклонить", () => Net.SendCommand(CmdType.Respond, pr.Id, 0)))
                : Ui.H(6, Ui.Btn("Отозвать", () => Net.SendCommand(CmdType.WithdrawProposal, pr.Id))));
            Add(Ui.Card(card));
        }
    }

    private static string DescribeTradeSide(TradeSide side)
    {
        var terms = new List<string>();
        if (side.Gold > 0) terms.Add($"{side.Gold} золота");
        if (side.Favor > 0) terms.Add($"{side.Favor} одобрения");
        if (side.Luxury != 0) terms.Add($"{Rules.Res((Resource)side.Luxury).Name} на 10 ходов");
        if (side.Tech.Length > 0) terms.Add(Rules.Tech(side.Tech)?.Name ?? side.Tech);
        return terms.Count == 0 ? "—" : string.Join(", ", terms);
    }

    private void ShowTradeComposer(int pid)
    {
        var dialog = new ConfirmationDialog
        {
            Title = $"Сделка с державой «{W.PlayerName(pid)}»",
            OkButtonText = "Отправить предложение",
            DialogHideOnOk = false,
        };
        var yours = TradeEditor("Вы отдаёте", false, out var readOffered);
        var theirs = TradeEditor("Вы получаете", true, out var readRequested);
        var columns = Ui.H(16, yours, new VSeparator(), theirs);
        var status = Ui.Wrap("", 540, 13, Ui.Dim);
        dialog.AddChild(Ui.V(10, columns, status));
        AddChild(dialog);
        dialog.GetCancelButton().Text = "Отмена";
        int seq = -1;
        void Result(int received, bool ok, string error, int rtt)
        {
            if (received != seq || dialog.IsQueuedForDeletion()) return;
            seq = -1;
            if (ok)
            {
                dialog.QueueFree();
                OpenDiplomacy(DiplomacyTab.Inbox);
                return;
            }
            status.Text = error;
            dialog.GetOkButton().Disabled = false;
        }
        Net.CommandResult += Result;
        dialog.TreeExiting += () => Net.CommandResult -= Result;
        dialog.Confirmed += () =>
        {
            if (seq >= 0) return;
            var offered = readOffered();
            var requested = readRequested();
            if (offered.IsEmpty && requested.IsEmpty)
            {
                status.Text = "Добавьте хотя бы одно условие сделки.";
                return;
            }
            if (Net.State != NetState.InGame)
            {
                status.Text = "Дождитесь восстановления связи.";
                return;
            }
            status.Text = "Отправляем предложение…";
            dialog.GetOkButton().Disabled = true;
            seq = Net.SendCommand(CmdType.Propose, pid, (int)ProposalKind.Trade, s: TradeTerms.Encode(offered, requested));
        };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered(new Vector2I(570, 315));
    }

    private Control TradeEditor(string title, bool request, out Func<TradeSide> read)
    {
        var panel = Ui.V(6, Ui.Lbl(title, 16, Ui.Gold, true));
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var gold = new SpinBox { MinValue = 0, MaxValue = request ? TradeTerms.MaxAmount : Math.Clamp(W.Me.Gold, 0, TradeTerms.MaxAmount), Step = 1 };
        var favor = new SpinBox { MinValue = 0, MaxValue = request ? TradeTerms.MaxAmount : Math.Clamp(W.Me.Favor, 0, TradeTerms.MaxAmount), Step = 1 };
        var lux = new OptionButton();
        var tech = new OptionButton();
        lux.AddItem("Без поставки", 0);
        foreach (var resource in Rules.Resources.Where(r => r.Class == ResClass.Luxury && (request || W.Me.Resources.Contains((int)r.Id))))
            lux.AddItem(resource.Name, (int)resource.Id);
        tech.AddItem("Без обмена", 0);
        var techIds = Rules.Techs.Where(t => request ? !W.Knows(t.Id) && t.Prereqs.All(W.Knows) : W.Me.Known.Contains(t.Id)).ToArray();
        for (int i = 0; i < techIds.Length; i++) tech.AddItem(techIds[i].Name, i + 1);
        panel.AddChild(Ui.V(3, Ui.Lbl("Золото", 12, Ui.Dim), gold));
        panel.AddChild(Ui.V(3, Ui.Lbl("Одобрение", 12, Ui.Dim), favor));
        panel.AddChild(Ui.V(3, Ui.Lbl("Роскошь · 10 ходов", 12, Ui.Dim), lux));
        panel.AddChild(Ui.V(3, Ui.Lbl("Технология", 12, Ui.Dim), tech));
        read = () => new TradeSide
        {
            Gold = (int)gold.Value, Favor = (int)favor.Value, Luxury = Math.Max(0, lux.GetSelectedId()),
            Tech = tech.Selected > 0 ? techIds[tech.Selected - 1].Id : "",
        };
        return panel;
    }

    private void BuildIntelligence()
    {
        bool unlocked = W.Knows("c_diplomacy");
        bool busy = W.Me.Espionage.Any(r => r.Observed < 0);
        Note(unlocked
            ? "Разведка стоит 20 одобрения. Через 3 хода вы получите снимок данных, который хранится ещё 5 ходов."
            : "Изучите «Дипломатическую службу» в разделе «Культура», чтобы отправлять разведку.");
        foreach (var r in W.Me.Espionage)
        {
            var card = Ui.V(4, Ui.Lbl(W.PlayerName(r.Target), 16, Ui.Gold, true));
            card.AddChild(r.Observed < 0
                ? Ui.Lbl($"Миссия завершится на ходу {r.Completes}", 13)
                : Ui.Wrap($"Снимок на ходу {r.Observed} · хранится до хода {r.Expires}\nКазна {r.Gold} · доход {Ui.Signed(r.GoldRate)} · {Rules.EraName(r.Era)} · технологий {r.Techs}", _wrapW - 24, 13));
            Add(Ui.Card(card));
        }
        foreach (var other in Contacts)
        {
            string? reason = !unlocked ? "Нужна Дипломатическая служба" : busy ? "Миссия уже выполняется"
                : W.Me.Favor < 20 ? "Нужно 20 одобрения" : null;
            var send = Ui.Btn("Отправить разведку", () => Net.SendCommand(CmdType.StartEspionage, other.Id), reason);
            send.Disabled = reason != null;
            Add(Ui.H(8, Ui.Lbl(other.Name, 14), Ui.Spacer(), send));
        }
    }

    private void TeachDiplomacyAndReligion()
    {
        foreach (var (ready, id, text) in new[]
        {
            (InboxCount > 0, "inbox", "Новое предложение. Откройте «Входящие» справа или в разделе «Державы» (F4)."),
            (Contacts.Any(), "contact", "Установлен контакт. В разделе «Державы» (F4) можно заключать договоры и сделки."),
            (W.Knows(Rules.ReligionCivic), "religion", "Обряды предков открыли религию и шаманство (F8)."),
        })
        {
            if (!ready || _main.Setting("tip_" + id, "0") == "1") continue;
            _main.SaveSetting("tip_" + id, "1");
            Log(text, Ui.Gold);
            Toast(text, false);
            break;
        }
    }
}
