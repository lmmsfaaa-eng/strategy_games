using System;
using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

public partial class GameScreen
{
    private VoiceChat _voice = null!;
    private AcceptDialog? _settingsDialog;
    private Label _micIndicator = null!;

    public bool CityFocused => _win == Win.City;

    private void ShowSettings()
    {
        if (_settingsDialog?.Visible == true) return;
        _settingsDialog?.QueueFree();
        var dialog = new AcceptDialog { Title = "Настройки", OkButtonText = "Закрыть", MinSize = new Vector2I(465, 200) };
        _settingsDialog = dialog;
        var content = Ui.V(8);
        var sound = new CheckButton { Text = "Звуки игры", ButtonPressed = !Sound.Muted };
        sound.Toggled += on => { Sound.Muted = !on; _main.SaveSetting("mute", on ? "0" : "1"); };
        content.AddChild(sound);
        var mode = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mode.AddItem("Выключен", (int)MicrophoneMode.Off);
        mode.AddItem("По удержанию", (int)MicrophoneMode.Hold);
        mode.AddItem("Всегда", (int)MicrophoneMode.Always);
        mode.Select((int)_voice.Mode);
        var bind = Ui.Btn(OS.GetKeycodeString(_voice.TalkKey), () => { });
        bind.Pressed += () =>
        {
            _voice.Rebinding = true;
            bind.Text = "Нажмите клавишу";
            bind.GrabFocus();
        };
        bind.GuiInput += e =>
        {
            if (!_voice.Rebinding || e is not InputEventKey { Pressed: true, Echo: false } key) return;
            _voice.Rebinding = false;
            if (key.Keycode != Key.Escape && key.PhysicalKeycode != Key.None)
                _voice.SetTalkKey(key.PhysicalKeycode);
            bind.Text = OS.GetKeycodeString(_voice.TalkKey);
            bind.AcceptEvent();
        };
        var bindRow = Ui.H(8, Ui.Lbl("Клавиша разговора", 13), Ui.Spacer(), bind);
        bindRow.Visible = _voice.Mode == MicrophoneMode.Hold;
        mode.ItemSelected += value =>
        {
            _voice.SetMode((MicrophoneMode)mode.GetItemId((int)value));
            bindRow.Visible = _voice.Mode == MicrophoneMode.Hold;
        };
        content.AddChild(Ui.H(12, Ui.Lbl("Микрофон", 14), mode));
        content.AddChild(bindRow);
        content.AddChild(new HSeparator());
        content.AddChild(Ui.Lbl("Громкость собеседников", 14, Ui.Gold));
        var speakers = Ui.V(5);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 120), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        speakers.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(speakers);
        content.AddChild(scroll);
        foreach (var p in W.Players.Values.Where(p => p.Id != W.MyId && !p.AI && !p.Neutral).OrderBy(p => p.Id))
        {
            var volume = new HSlider { MaxValue = 100, Step = 1, Value = _voice.GetVolume(p.Id), CustomMinimumSize = new Vector2(128, 24) };
            var amount = Ui.Lbl($"{volume.Value}%");
            amount.CustomMinimumSize = new Vector2(44, 0);
            volume.ValueChanged += v => { _voice.SetVolume(p.Id, (int)v); amount.Text = $"{v}%"; };
            speakers.AddChild(Ui.H(8, Ui.Lbl(VoiceChat.PlayerName(p), 14), Ui.Spacer(), volume, amount));
        }
        if (speakers.GetChildCount() == 0) speakers.AddChild(Ui.Lbl("В партии пока нет собеседников", 13, Ui.Dim));
        dialog.VisibilityChanged += () =>
        {
            if (dialog.Visible) return;
            _voice.Rebinding = false;
            foreach (var p in W.Players.Values.Where(p => p.Id != W.MyId && !p.AI && !p.Neutral))
                _voice.SaveVolume(p.Id);
        };
        dialog.AddChild(content);
        AddChild(dialog);
        dialog.PopupCentered();
    }

}
