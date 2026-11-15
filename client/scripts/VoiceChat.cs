using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;
using IH.Shared;

namespace IH.Client;

public enum MicrophoneMode { Off, Hold, Always }

/// <summary>Lives for one game screen and owns the microphone capture and each speaker's playback.</summary>
public sealed partial class VoiceChat : Node
{
    private sealed class Speaker
    {
        public AudioStreamPlayer Player = null!;
        public AudioStreamGeneratorPlayback Buffer = null!;
        public uint Sequence;
        public ulong LastFrame;
        public bool HasFrame;
    }

    private const string CaptureBus = "IH Voice Capture";
    private readonly Main _main;
    private readonly ClientWorld _world;
    private readonly Dictionary<int, Speaker> _speakers = new();
    private readonly Dictionary<int, int> _volume = new();
    private readonly byte[] _pcm = new byte[VoiceWire.PcmBytes];
    private readonly Vector2[] _playbackFrame = new Vector2[VoiceWire.Samples];
    private AudioStreamPlayer? _mic;
    private AudioEffectCapture? _capture;
    private int _bus = -1;
    private uint _sequence;
    public MicrophoneMode Mode { get; private set; }
    public Key TalkKey { get; private set; }
    public bool Capturing => _mic?.Playing == true;
    public bool Rebinding { get; set; }

    public VoiceChat(Main main, ClientWorld world)
    {
        _main = main;
        _world = world;
        if (Enum.TryParse(main.Setting("mic_mode", "Off"), out MicrophoneMode mode) && Enum.IsDefined(mode)) Mode = mode;
        TalkKey = Enum.TryParse(main.Setting("mic_key", "V"), out Key key) && key != Key.None ? key : Key.V;
    }

    public override void _Ready()
    {
        _main.Net.VoiceReceived += Receive;
        _main.Net.StateChanged += ConnectionChanged;
    }

    public override void _ExitTree()
    {
        _main.Net.VoiceReceived -= Receive;
        _main.Net.StateChanged -= ConnectionChanged;
        StopCapture();
        if (_bus >= 0 && AudioServer.GetBusName(_bus) == CaptureBus) AudioServer.RemoveBus(_bus);
    }

    public void SetMode(MicrophoneMode mode)
    {
        Mode = mode;
        _main.SaveSetting("mic_mode", mode.ToString());
        if (mode != MicrophoneMode.Always) StopCapture();
    }

    public void SetTalkKey(Key key)
    {
        TalkKey = key;
        _main.SaveSetting("mic_key", key.ToString());
    }

    private string VolumeKey(int id) =>
        "voice_" + (_world.Players.TryGetValue(id, out var p) ? PlayerName(p) : id.ToString());

    internal static string PlayerName(PlayerDto p) => string.IsNullOrEmpty(p.ParticipantName) ? p.Name : p.ParticipantName;

    public int GetVolume(int id)
    {
        if (_volume.TryGetValue(id, out int saved)) return saved;
        saved = int.TryParse(_main.Setting(VolumeKey(id), "100"), out int value) ? Math.Clamp(value, 0, 100) : 100;
        _volume[id] = saved;
        return saved;
    }

    public void SetVolume(int id, int volume)
    {
        _volume[id] = volume;
    }

    public void SaveVolume(int id) => _main.SaveSetting(VolumeKey(id), GetVolume(id).ToString());

    private void ConnectionChanged(NetState state)
    {
        if (state == NetState.InGame) return;
        StopCapture();
        foreach (var speaker in _speakers.Values) speaker.Player.QueueFree();
        _speakers.Clear();
    }

    public override void _Process(double delta)
    {
        bool sending = _main.Net.State == NetState.InGame && !Rebinding && (Mode == MicrophoneMode.Always ||
            (Mode == MicrophoneMode.Hold && GetWindow().HasFocus() && Input.IsPhysicalKeyPressed(TalkKey)));
        if (!sending) { StopCapture(); return; }
        if (!Capturing) StartCapture();
        if (!Capturing || _capture == null) return;
        int sourceFrames = Math.Max(1, (int)Math.Round(AudioServer.GetMixRate() / 50d));
        for (int i = 0; i < 5 && _capture.CanGetBuffer(sourceFrames); i++)
        {
            var source = _capture.GetBuffer(sourceFrames);
            for (int n = 0; n < VoiceWire.Samples; n++)
            {
                float at = n * (source.Length / (float)VoiceWire.Samples);
                int left = (int)at;
                var sample = source[left].Lerp(source[Math.Min(left + 1, source.Length - 1)], at - left);
                short mono = (short)(Math.Clamp((sample.X + sample.Y) * 0.5f, -1f, 1f) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(_pcm.AsSpan(n * 2), mono);
            }
            _main.Net.SendVoice(++_sequence, _pcm);
        }
    }

    private void StartCapture()
    {
        if (_mic == null)
        {
            // Remove a stale capture bus if the game screen changed in this frame.
            int existing = AudioServer.GetBusIndex(CaptureBus);
            if (existing >= 0) return;
            AudioServer.AddBus();
            _bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(_bus, CaptureBus);
            AudioServer.SetBusVolumeDb(_bus, -80);
            _capture = new AudioEffectCapture { BufferLength = 0.16f };
            AudioServer.AddBusEffect(_bus, _capture);
            _mic = new AudioStreamPlayer { Bus = CaptureBus, Stream = new AudioStreamMicrophone() };
            AddChild(_mic);
        }
        _capture!.ClearBuffer();
        _mic.Play();
    }

    private void StopCapture()
    {
        if (!Capturing) return;
        _mic!.Stop();
        _capture!.ClearBuffer();
    }

    private void Receive(int id, uint sequence, ReadOnlyMemory<byte> frame)
    {
        if (!_world.Players.TryGetValue(id, out var participant) || participant.AI || participant.Neutral) return;
        if (!_speakers.TryGetValue(id, out var speaker))
        {
            var player = new AudioStreamPlayer
            {
                Stream = new AudioStreamGenerator
                {
                    MixRateMode = AudioStreamGenerator.AudioStreamGeneratorMixRate.Custom,
                    MixRate = VoiceWire.SampleRate,
                    BufferLength = 0.12f,
                },
            };
            AddChild(player);
            player.Play();
            if (player.GetStreamPlayback() is not AudioStreamGeneratorPlayback playback) { player.QueueFree(); return; }
            _speakers[id] = speaker = new Speaker { Player = player, Buffer = playback };
        }
        ulong now = Time.GetTicksMsec();
        if (speaker.HasFrame && now - speaker.LastFrame < 1000 && unchecked((int)(sequence - speaker.Sequence)) <= 0) return;
        speaker.Sequence = sequence;
        speaker.LastFrame = now;
        speaker.HasFrame = true;
        float volume = GetVolume(id) / 100f;
        speaker.Player.VolumeDb = volume == 0 ? -80 : Mathf.LinearToDb(volume);
        if (volume == 0 || !speaker.Buffer.CanPushBuffer(VoiceWire.Samples)) return;
        for (int i = 0; i < VoiceWire.Samples; i++)
        {
            float s = BinaryPrimitives.ReadInt16LittleEndian(frame.Span[(i * 2)..]) / 32768f;
            _playbackFrame[i] = new Vector2(s, s);
        }
        speaker.Buffer.PushBuffer(_playbackFrame);
    }
}
