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
    private readonly HBoxContainer _nationHud = Ui.H(4);
    private readonly Control _nationAnchor = new() { MouseFilter = MouseFilterEnum.Ignore };
    private ScrollContainer _nationScroll = null!;
    private string _nationHudState = "";
    private const int NationHudHeight = 108;

    private IEnumerable<PlayerDto> Contacts => W.Players.Values
        .Where(p => p.Id != W.MyId && p.Alive && !p.Neutral && p.Met).OrderBy(p => p.Id);

    private int InboxCount => W.Me.Proposals.Count(p => p.To == W.MyId) + W.Me.DefenseCalls.Length;

    private void OpenDiplomacy(DiplomacyTab tab, int nation = -1)
    {
        _diplomacyTab = tab;
        if (nation >= 0) _nation = nation;
        Toggle(Win.Diplomacy, true);
    }

    private void BuildNationHud()
    {
        _nationScroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
        _nationScroll.AddChild(_nationHud);
        AddChild(_nationScroll);
        _nationAnchor.ItemRectChanged += AlignNationHud;
    }

    private void AlignNationHud()
    {
        _nationScroll.GlobalPosition = _nationAnchor.GlobalPosition;
        _nationScroll.Size = new Vector2(_nationAnchor.Size.X, NationHudHeight);
    }

    private void RefreshNationHud()
    {
        var shared = W.Players.Values.Where(p => p.Alive && !p.Neutral && (p.Met || !p.AI || p.Reputation >= 0))
            .OrderBy(p => p.Id != W.MyId).ThenBy(p => p.Id).ToArray();
        int incoming = InboxCount;
        float limit = Math.Clamp(GetViewportRect().Size.X * 0.23f, 80, 322);
        _nationAnchor.CustomMinimumSize = new Vector2(Math.Min(limit, shared.Length * 76 + (incoming > 0 ? 42 : 0)), 0);
        string state = $"{W.CurrentPlayer}:{incoming}|" + string.Join("|", shared.Select(p =>
            $"{p.Id}:{VoiceChat.PlayerName(p)}:{p.Color}:{p.Connected}:{p.EndedTurn}:{p.Met}:{p.Reputation}:{p.GoldRate}:{p.SharedScience}:{p.SharedCulture}:{p.SharedStats}"));
        if (state == _nationHudState) return;
        _nationHudState = state;
        int scroll = _nationScroll.ScrollHorizontal;
        Ui.Clear(_nationHud);
        _nationScroll.Visible = shared.Length > 0 || incoming > 0;
        if (!_nationScroll.Visible) return;
        if (incoming > 0)
        {
            var inbox = Ui.Btn($"✉ {incoming}", () => OpenDiplomacy(DiplomacyTab.Inbox), "Предложения и призывы к защите", 12, "Primary");
            inbox.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            _nationHud.AddChild(inbox);
        }
        foreach (var p in shared)
        {
            string name = VoiceChat.PlayerName(p);
            bool pending = W.Settings.Simultaneous ? !p.EndedTurn : W.CurrentPlayer == p.Id;
            Color fill = p.Connected || p.AI ? Map.OwnerColor(p.Id).Darkened(0.52f) : Ui.Bg;
            var holder = new PanelContainer { CustomMinimumSize = new Vector2(27, 27), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
            holder.AddThemeStyleboxOverride("panel", Ui.Box(fill, pending ? Ui.Gold : null, 16, 0, 0, 2));
            string initial = string.IsNullOrWhiteSpace(name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(name.TrimStart()).ToUpperInvariant();
            var label = Ui.Lbl(initial, 13, Ui.Text, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            holder.AddChild(label);

            float score = Math.Max(0, p.Reputation) / 100f;
            var color = score < 0.5f ? Ui.Bad.Lerp(Ui.Gold, score * 2) : Ui.Gold.Lerp(Ui.Good, (score - .5f) * 2);
            var reputation = new Bar(color, 4, 50).WithBack(Ui.Line).Set(score);
            string standing = p.Reputation < 0 ? "Порядочность: пока нет истории" : $"Порядочность: {p.Reputation}/100";
            string tip = $"{name} · {(pending ? "Ход в процессе" : "Ход завершён")}\n{standing}";
            var info = Ui.V(2, holder, reputation);
            if (Shows(p, StatAccess.Economy))
            {
                info.AddChild(new Chip(Ico.Gold, Ui.Gold, "Золото за ход", 12).Set(Ui.Signed(p.GoldRate)));
                tip += $"\nЗолото за ход: {Ui.Signed(p.GoldRate)}";
            }
            if (Shows(p, StatAccess.Development))
            {
                info.AddChild(new Chip(Ico.Science, Ui.Science, "Наука за ход", 12).Set(Ui.Signed(p.SharedScience)));
                info.AddChild(new Chip(Ico.Culture, Ui.Culture, "Культура за ход", 12).Set(Ui.Signed(p.SharedCulture)));
                tip += $"\nНаука за ход: {Ui.Signed(p.SharedScience)} · культура: {Ui.Signed(p.SharedCulture)}";
            }
            var card = p.Id != W.MyId && p.Met ? Ui.Clickable(info, () => ShowTradeComposer(p.Id), tip, margin: 4) : Ui.Card(info, 4);
            card.TooltipText = tip;
            card.CustomMinimumSize = new Vector2(72, 0);
            card.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            _nationHud.AddChild(card);
        }
        AlignNationHud();
        _nationScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, scroll);
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
                p.AtWar ? "Война" : p.Friendly ? "Дружба" : p.Allied ? "Оборонительный союз" : "Мир", 13, p.Id == _nation ? "TabOn" : "Tab"));
        Add(names);
        var other = contacts.First(p => p.Id == _nation);
        string relation = other.AtWar ? "Война" : other.Friendly ? "Дружба" : other.Allied ? "Оборонительный союз" : "Мир";
        Add(Ui.H(8, Ui.Lbl(other.Name, 20, Ui.Col(other.Color).Lightened(0.35f), true), Ui.Pill(relation, other.AtWar ? Ui.Bad : Ui.Good),
            Ui.Lbl(other.AI ? "ИИ" : other.Connected ? "Игрок в сети" : "Игрок не в сети", 12, Ui.Dim)));

        Heading("Договоры");
        var imports = W.Me.LuxuryAgreements.Where(x => x.From == other.Id || x.To == other.Id).ToArray();
        var terms = new List<string>();
        if (other.OpenBorders) terms.Add("Открытые границы");
        if (other.GuaranteesPeace) terms.Add("Вы гарантировали ненападение");
        if (other.PeaceGuaranteed) terms.Add("Вам гарантировали ненападение");
        if (other.Embargo) terms.Add("Ваше эмбарго");
        if (other.Embargoed) terms.Add("Их эмбарго");
        terms.AddRange(imports.Select(x => $"{(x.To == W.MyId ? "Получаем" : "Поставляем")} {Rules.Res((Resource)x.Resource).Name} · до хода {x.Expires}"));
        Note(terms.Count > 0 ? string.Join(" · ", terms) : "Действующих поставок и дополнительных соглашений нет.");

        var actions = Ui.Flow(6);
        var openOffer = Ui.Btn("Открыть сделку", () => ShowTradeComposer(other.Id), "Предложения, действующие договоры и претензии", variation: "Primary");
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
            war.Disabled = other.Allied || other.GuaranteesPeace;
            if (war.Disabled) war.TooltipText = other.Allied ? "Сначала расторгните оборонительный союз" : "Сначала прекратите свою гарантию ненападения в окне сделки";
            actions.AddChild(war);
        }
        actions.AddChild(Ui.Btn(other.Embargo ? "Снять эмбарго" : "Ввести эмбарго",
            () => Net.SendCommand(CmdType.SetEmbargo, pid, other.Embargo ? 0 : 1),
            other.Embargo ? "Снова разрешить сделки со своей стороны" : "Заблокировать новые сделки. Договоры с гарантиями ненападения сохранятся"));
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
            var join = Ui.Btn("Вступить в войну", () => Net.SendCommand(CmdType.RespondDefense, call.Id, 1), variation: "Danger");
            if (W.Players.TryGetValue(call.Aggressor, out var aggressor) && aggressor.GuaranteesPeace)
            {
                join.Disabled = true;
                join.TooltipText = "Вступить в войну мешает гарантия ненападения";
            }
            Add(Ui.Card(Ui.V(6, Ui.Lbl($"Призыв к защите · {W.PlayerName(call.Ally)}", 16, Ui.Gold, true),
                Ui.Lbl($"Вступить в войну против «{call.AggressorName}» · до хода {call.Expires}", 13),
                Ui.H(6, join, Ui.Btn("Отклонить", () => Net.SendCommand(CmdType.RespondDefense, call.Id, 0))))));
        }
        foreach (var pr in W.Me.Proposals.OrderBy(p => p.To != W.MyId).ThenBy(p => p.Expires))
        {
            bool incoming = pr.To == W.MyId;
            var kind = (ProposalKind)pr.Kind;
            string title = kind switch
            {
                ProposalKind.Peace => "Мир", ProposalKind.OpenBorders => "Открытые границы",
                ProposalKind.Alliance => "Оборонительный союз", _ => TradeTerms.IntentName(TradeTerms.Intent(pr.Offered, pr.Requested)),
            };
            var card = Ui.V(6, Ui.Lbl($"{title} · {W.PlayerName(incoming ? pr.From : pr.To)}", 16, Ui.Gold, true),
                Ui.Lbl($"{(incoming ? "Входящее" : "Отправлено")} · до хода {pr.Expires}", 12, Ui.Dim));
            if (kind == ProposalKind.Trade)
            {
                card.AddChild(Ui.Wrap("Вы отдаёте · " + DescribeTradeSide(incoming ? pr.Requested : pr.Offered), _wrapW - 26, 13));
                card.AddChild(Ui.Wrap("Вы получаете · " + DescribeTradeSide(incoming ? pr.Offered : pr.Requested), _wrapW - 26, 13));
            }
            card.AddChild(incoming ? OfferButtons(pr) : Ui.H(6, Ui.Btn("Отозвать",
                () => Net.SendCommand(CmdType.WithdrawProposal, pr.Id))));
            Add(Ui.Card(card));
        }
    }

    private Control OfferButtons(ProposalDto proposal, Action? close = null)
    {
        void Respond(bool accepted)
        {
            Net.SendCommand(CmdType.Respond, proposal.Id, accepted ? 1 : 0);
            close?.Invoke();
        }
        var row = Ui.Flow(6);
        row.AddChild(Ui.Btn("Принять", () => Respond(true), variation: "Primary"));
        row.AddChild(Ui.Btn("Отклонить", () => Respond(false)));
        if (proposal.Kind == (byte)ProposalKind.Trade)
            row.AddChild(Ui.Btn("Встречное предложение", () => { close?.Invoke(); ShowTradeComposer(proposal.From, proposal); }));
        return row;
    }

    private Control ContractCard(ContractDto contract, Window parent)
    {
        var root = Ui.V(5, Ui.Lbl("Действующий договор", 14, Ui.Gold, true));
        bool sender = contract.From == W.MyId;
        var offered = sender ? contract.Offered : contract.Requested;
        var received = sender ? contract.Requested : contract.Offered;
        string Describe(IEnumerable<DealClause> clauses) => string.Join(", ", clauses.Select(TradeTerms.ClauseName));
        if (offered.Length != 0) root.AddChild(Ui.Wrap("Вы предоставляете: " + Describe(offered), 510, 12));
        if (received.Length != 0) root.AddChild(Ui.Wrap("Получаете: " + Describe(received), 510, 12));
        void Manage(ContractAction action)
        {
            Net.SendCommand(CmdType.ManageContract, contract.Id, (int)action);
            parent.QueueFree();
        }
        var actions = Ui.Flow(6);
        if (contract.EndRequestedBy == W.MyId) actions.AddChild(Ui.Lbl("Ожидаем ответа", 12, Ui.Dim));
        else if (contract.EndRequestedBy >= 0)
        {
            actions.AddChild(Ui.Btn("Согласиться прекратить", () => Manage(ContractAction.AcceptEnd)));
            actions.AddChild(Ui.Btn("Сохранить договор", () => Manage(ContractAction.DeclineEnd)));
        }
        else actions.AddChild(Ui.Btn("Предложить прекращение", () => Manage(ContractAction.ProposeEnd)));
        if (offered.Length > 0 || received.Any(c => c.Kind != (byte)DealKind.NonAggression))
            actions.AddChild(Ui.Btn("Нарушить договор", () =>
        {
            var warning = new ConfirmationDialog { Title = "Нарушить договор?", OkButtonText = "Нарушить",
                DialogText = "Это повлияет на вашу репутацию. Об этом узнают все игроки.\nПрекратятся: " + Describe(offered.Concat(received)) };
            parent.AddChild(warning);
            warning.GetCancelButton().Text = "Отмена";
            warning.Canceled += warning.QueueFree;
            warning.Confirmed += () => Manage(ContractAction.Breach);
            warning.PopupCentered();
        }, variation: "Danger"));
        root.AddChild(actions);
        return Ui.Card(root);
    }

    private void HandleContractNotice(EventDto item)
    {
        if (item.Type == EventKind.ContractBreach) { Toast(item.S, true); return; }
        if (item.Type is not (EventKind.ContractOffer or EventKind.ContractGift or EventKind.ContractHarm)) return;
        var dialog = new AcceptDialog
        {
            Title = item.Type == EventKind.ContractOffer ? "Предложение об обмене" : "Договор",
            OkButtonText = "Закрыть",
        };
        if (item.Type == EventKind.ContractOffer)
        {
            var pr = W.Me.Proposals.FirstOrDefault(p => p.Id == item.A && p.To == W.MyId);
            if (pr == null) { dialog.QueueFree(); return; }
            dialog.Title = TradeTerms.IntentName(TradeTerms.Intent(pr.Offered, pr.Requested));
            dialog.AddChild(Ui.V(7,
                Ui.Lbl(W.PlayerName(pr.From), 16, Ui.Gold, true),
                Ui.Wrap("Вы отдаёте: " + DescribeTradeSide(pr.Requested), 420, 13),
                Ui.Wrap("Вы получаете: " + DescribeTradeSide(pr.Offered), 420, 13),
                OfferButtons(pr, dialog.QueueFree)));
        }
        else dialog.AddChild(Ui.Wrap(item.S, 420, 15));
        AddChild(dialog);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    private static string DescribeTradeSide(TradeSide side)
    {
        var terms = new List<string>();
        if (side.Gold > 0) terms.Add($"{side.Gold} золота");
        if (side.Favor > 0) terms.Add($"{side.Favor} одобрения");
        if (side.Luxury != 0) terms.Add($"{Rules.Res((Resource)side.Luxury).Name} на 10 ходов");
        if (side.Tech.Length > 0) terms.Add(Rules.Tech(side.Tech)?.Name ?? side.Tech);
        terms.AddRange(side.Clauses.Select(TradeTerms.ClauseName));
        return terms.Count == 0 ? "—" : string.Join(", ", terms);
    }

    private void ShowTradeComposer(int pid, ProposalDto? counter = null)
    {
        if (!W.Players.TryGetValue(pid, out var nation) || !nation.Met || pid == W.MyId) return;
        var dialog = new AcceptDialog
        {
            Title = $"Сделка с державой «{W.PlayerName(pid)}»",
            DialogHideOnOk = false,
        };
        dialog.GetOkButton().Hide();
        int seq = -1;
        bool blocked = nation.AtWar || nation.Embargo || nation.Embargoed;
        var friendship = new CheckButton { Text = "Дружба", ButtonPressed = counter?.Offered.HasFriendship == true,
            Visible = !nation.Friendly, Disabled = blocked, TooltipText = "+1 одобрение за ход каждой державе. Можно прекратить в любой момент." };
        var yours = TradeEditor("Вы отдаёте", false, counter?.Requested, friendship, out var readOffered);
        var theirs = TradeEditor("Вы получаете", true, counter?.Offered, friendship, out var readRequested);
        var send = new Button { Text = "Обмен", ThemeTypeVariation = "Primary" };
        var center = Ui.V(6, send, friendship);
        var columns = Ui.H(10, yours, new VSeparator(), center, new VSeparator(), theirs);
        var status = Ui.Wrap("", 540, 13, Ui.Dim);
        var content = Ui.V(10, columns, status);
        if (nation.Friendly) content.AddChild(Ui.Btn("Прекратить дружбу", () => SendOther(CmdType.EndFriendship), "Без штрафа"));
        if (nation.HasClaim) content.AddChild(Ui.Btn("Отказаться от претензий", () => SendOther(CmdType.WaiveClaims),
            "Снять влияние нарушений перед вашей державой на порядочность контрагента"));
        foreach (var contract in W.Me.Contracts.Where(c => c.From == pid || c.To == pid))
            content.AddChild(ContractCard(contract, dialog));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(675, Math.Clamp(GetViewportRect().Size.Y - 190, 240, 540)) };
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);
        dialog.AddChild(scroll);
        AddChild(dialog);
        if (blocked) status.Text = nation.AtWar ? "Предлагать новые условия можно после заключения мира." : "Новые сделки заблокированы эмбарго.";
        void RefreshAction()
        {
            var intent = TradeTerms.Intent(readOffered(), readRequested());
            send.Text = TradeTerms.IntentName(intent);
            send.Disabled = blocked || seq >= 0 || intent == TradeIntent.Empty;
        }
        foreach (var editor in new[] { yours, theirs })
        {
            foreach (var node in editor.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>())
                node.Pressed += RefreshAction;
            foreach (var node in editor.FindChildren("*", "SpinBox", true, false).OfType<SpinBox>())
                node.ValueChanged += _ => RefreshAction();
            foreach (var node in editor.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>())
                node.ItemSelected += _ => RefreshAction();
        }
        friendship.Toggled += _ => RefreshAction();
        RefreshAction();
        void Result(int received, bool ok, string error, int rtt)
        {
            if (received != seq || dialog.IsQueuedForDeletion()) return;
            seq = -1;
            if (ok)
            {
                dialog.QueueFree();
                return;
            }
            status.Text = error;
            RefreshAction();
        }
        Net.CommandResult += Result;
        dialog.TreeExiting += () => Net.CommandResult -= Result;
        void Submit()
        {
            if (blocked || seq >= 0) return;
            var offered = readOffered();
            var requested = readRequested();
            if (TradeTerms.Intent(offered, requested) == TradeIntent.Empty) return;
            if (Net.State != NetState.InGame)
            {
                status.Text = "Дождитесь восстановления связи.";
                return;
            }
            status.Text = "Отправляем предложение…";
            seq = Net.SendCommand(CmdType.OfferContract, pid, counter?.Id ?? 0, s: TradeTerms.Encode(offered, requested));
            RefreshAction();
        }
        void SendOther(CmdType command)
        {
            if (seq >= 0 || Net.State != NetState.InGame) return;
            seq = Net.SendCommand(command, pid);
            RefreshAction();
        }
        send.Pressed += Submit;
        dialog.Canceled += dialog.QueueFree;
        dialog.CloseRequested += dialog.QueueFree;
        dialog.PopupCentered(new Vector2I(700, 315));
    }

    private Control TradeEditor(string title, bool request, TradeSide? initial, CheckButton friendship, out Func<TradeSide> read)
    {
        var panel = Ui.V(6, Ui.Lbl(title, 16, Ui.Gold, true));
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var gold = new SpinBox { MinValue = 0, MaxValue = request ? TradeTerms.MaxAmount : Math.Clamp(W.Me.Gold, 0, TradeTerms.MaxAmount), Step = 1 };
        var favor = new SpinBox { MinValue = 0, MaxValue = request ? TradeTerms.MaxAmount : Math.Clamp(W.Me.Favor, 0, TradeTerms.MaxAmount), Step = 1 };
        var lux = new OptionButton();
        var tech = new OptionButton();
        lux.AddItem("Выберите ресурс", 0);
        foreach (var resource in Rules.Resources.Where(r => r.Id != Resource.None && (request || W.Me.Resources.Contains((int)r.Id))))
            lux.AddItem(resource.Name, (int)resource.Id);
        tech.AddItem("Без обмена", 0);
        var techIds = Rules.Techs.Where(t => request ? !W.Knows(t.Id) && t.Prereqs.All(W.Knows) : W.Me.Known.Contains(t.Id)).ToArray();
        for (int i = 0; i < techIds.Length; i++) tech.AddItem(techIds[i].Name, i + 1);
        var selected = Ui.V(4);
        var available = Ui.V(3);
        panel.AddChild(selected);
        var friendshipTerm = Ui.Lbl("Дружба · +1 одобрение за ход", 12, Ui.Good);
        selected.AddChild(friendshipTerm);
        friendshipTerm.Visible = friendship.ButtonPressed;
        friendship.Toggled += on => friendshipTerm.Visible = on;
        panel.AddChild(new HSeparator());
        panel.AddChild(available);
        var chosen = new HashSet<string>();
        void Term(string id, string label, Control editor, bool enabled)
        {
            var add = new Button { Text = label, TooltipText = "Добавить условие" };
            var remove = new Button { Text = "×", TooltipText = "Убрать условие" };
            var item = Ui.Card(Ui.V(3, Ui.H(4, Ui.Lbl(label, 12), Ui.Spacer(), remove), editor), 4);
            selected.AddChild(item);
            available.AddChild(add);
            add.Pressed += () => Pick(true);
            remove.Pressed += () => Pick(false);
            Pick(enabled);
            void Pick(bool on)
            {
                if (on) chosen.Add(id); else chosen.Remove(id);
                item.Visible = on;
                add.Visible = !on;
            }
        }
        DealClause? Preset(DealKind kind) => initial?.Clauses.FirstOrDefault(c => c.Kind == (byte)kind);
        OptionButton Duration(DealKind kind)
        {
            var result = new OptionButton();
            foreach (int length in new[] { 5, 10, 20, 0 })
                result.AddItem(length == 0 ? "Бессрочно" : $"{length} х.", length);
            result.Select(result.GetItemIndex(Preset(kind)?.Turns ?? 10));
            return result;
        }
        gold.Value = initial?.Gold ?? 0;
        favor.Value = initial?.Favor ?? 0;
        tech.Select(Math.Max(0, Array.FindIndex(techIds, t => t.Id == initial?.Tech) + 1));
        lux.Select(Math.Max(0, lux.GetItemIndex(Preset(DealKind.Resource)?.Value ?? initial?.Luxury ?? 0)));
        var perTurn = new SpinBox { MinValue = 1, MaxValue = TradeTerms.MaxAmount, Step = 1, Value = Preset(DealKind.GoldPerTurn)?.Value ?? 1 };
        var stats = new OptionButton();
        stats.AddItem("Экономика", (int)StatAccess.Economy);
        stats.AddItem("Развитие", (int)StatAccess.Development);
        stats.AddItem("Территория", (int)StatAccess.Territory);
        stats.AddItem("Все показатели", (int)StatAccess.All);
        stats.Select(Math.Max(0, stats.GetItemIndex(Preset(DealKind.Statistics)?.Value ?? 1)));
        var goldDuration = Duration(DealKind.GoldPerTurn);
        var resourceDuration = Duration(DealKind.Resource);
        var accessDuration = Duration(DealKind.Statistics);
        var passageDuration = Duration(DealKind.Passage);
        var allianceDuration = Duration(DealKind.Alliance);
        var guaranteeDuration = Duration(DealKind.NonAggression);
        Term("gold", "Золото", gold, gold.Value > 0);
        Term("favor", "Одобрение", favor, favor.Value > 0);
        Term("tech", "Технология", tech, tech.Selected > 0);
        Term("payment", "Золото за ход", Ui.H(3, perTurn, goldDuration), Preset(DealKind.GoldPerTurn) != null);
        Term("resource", "Поставки ресурсов", Ui.H(3, lux, resourceDuration), lux.Selected > 0);
        Term("stats", "Доступ к показателям", Ui.H(3, stats, accessDuration), Preset(DealKind.Statistics) != null);
        Term("passage", "Право прохода", passageDuration, Preset(DealKind.Passage) != null);
        Term("alliance", "Оборонительный союз", allianceDuration, Preset(DealKind.Alliance) != null);
        Term("guarantee", "Гарантия ненападения", guaranteeDuration, Preset(DealKind.NonAggression) != null);
        DealClause Clause(DealKind kind, int value, OptionButton duration) =>
            new() { Kind = (byte)kind, Value = value, Turns = duration.GetSelectedId() };
        read = () => new TradeSide
        {
            Gold = chosen.Contains("gold") ? (int)gold.Value : 0, Favor = chosen.Contains("favor") ? (int)favor.Value : 0,
            Tech = chosen.Contains("tech") && tech.Selected > 0 ? techIds[tech.Selected - 1].Id : "",
            Clauses = new[]
            {
                chosen.Contains("payment") ? Clause(DealKind.GoldPerTurn, (int)perTurn.Value, goldDuration) : null,
                chosen.Contains("resource") && lux.Selected > 0 ? Clause(DealKind.Resource, lux.GetSelectedId(), resourceDuration) : null,
                chosen.Contains("stats") ? Clause(DealKind.Statistics, stats.GetSelectedId(), accessDuration) : null,
                chosen.Contains("passage") ? Clause(DealKind.Passage, 1, passageDuration) : null,
                chosen.Contains("alliance") ? Clause(DealKind.Alliance, 1, allianceDuration) : null,
                chosen.Contains("guarantee") ? Clause(DealKind.NonAggression, 1, guaranteeDuration) : null,
                friendship.ButtonPressed ? new DealClause { Kind = (byte)DealKind.Friendship, Value = 1 } : null,
            }.OfType<DealClause>().ToArray(),
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
