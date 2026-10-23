using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tuner.Core.Audio;
using Tuner.Core.Ducking;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Tuner;

/// <summary>实时状态页：按 InstanceId 差量更新，避免整表重建。</summary>
public partial class MainWindow
{
    /// <summary>状态行视图模型：按属性名精确通知（峰值每拍都在变，整行广播会让全部绑定重求值）。</summary>
    private sealed class StatusRow : INotifyPropertyChanged
    {
        public string InstanceId = "";
        private string _process = "";
        private string _pidText = "";
        private string _groupName = "";
        private Brush _groupBrush = IdleBrushStatic;
        private Brush _speakingBrush = IdleBrushStatic;
        private Visibility _duckVisibility = Visibility.Collapsed;
        private string _duckText = "";
        private double _volumePercent;
        private string _volumeText = "";
        private Brush _volBrush = IdleBrushStatic;

        private static readonly Brush IdleBrushStatic = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));

        private string _channel = "";
        private double _peakPercent;

        public string Channel { get => _channel; set { if (value == _channel) return; _channel = value; Pc(); } }
        public double PeakPercent { get => _peakPercent; set { if (value == _peakPercent) return; _peakPercent = value; Pc(); } }

        private double _stripOpacity = 0.25;
        /// <summary>通道色条透明度：出声时点亮。</summary>
        public double StripOpacity { get => _stripOpacity; set { if (value == _stripOpacity) return; _stripOpacity = value; Pc(); } }

        private BitmapImage? _icon;
        /// <summary>应用图标。</summary>
        public BitmapImage? Icon { get => _icon; set { if (ReferenceEquals(value, _icon)) return; _icon = value; Pc(); } }

        /// <summary>已解码图标对应的 PNG 源（引用不变则不重复解码）。</summary>
        public byte[]? IconPngSource;

        public string Process { get => _process; set { if (value == _process) return; _process = value; Pc(); } }
        public string PidText { get => _pidText; set { var t = $"PID {value}"; if (t == _pidText) return; _pidText = t; Pc(); } }
        public string GroupName { get => _groupName; set { if (value == _groupName) return; _groupName = value; Pc(); } }
        public Brush GroupBrush { get => _groupBrush; set { if (ReferenceEquals(value, _groupBrush)) return; _groupBrush = value; Pc(); } }
        public Brush SpeakingBrush { get => _speakingBrush; set { if (ReferenceEquals(value, _speakingBrush)) return; _speakingBrush = value; Pc(); } }
        public Visibility DuckVisibility { get => _duckVisibility; set { if (value == _duckVisibility) return; _duckVisibility = value; Pc(); } }
        public string DuckText { get => _duckText; set { if (value == _duckText) return; _duckText = value; Pc(); } }
        public double VolumePercent { get => _volumePercent; set { if (value == _volumePercent) return; _volumePercent = value; Pc(); } }
        public string VolumeText { get => _volumeText; set { if (value == _volumeText) return; _volumeText = value; Pc(); } }
        public Brush VolBrush { get => _volBrush; set { if (ReferenceEquals(value, _volBrush)) return; _volBrush = value; Pc(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Pc([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly ObservableCollection<StatusRow> _statusRows = new();
    private readonly Dictionary<string, StatusRow> _statusById = new();
    private string _statusSignature = "";

    private void RefreshStatus()
    {
        var (sessions, states) = BuildRefreshInputs();
        RefreshStatus(sessions, states);
    }

    private void RefreshStatus(List<SoundSession> sessions,
        Dictionary<string, DuckingEngine.SessionState> states)
    {
        var sig = string.Join("|", sessions.Select(s => s.InstanceId));
        if (sig != _statusSignature)
        {
            _statusSignature = sig;
            _statusRows.Clear();
            _statusById.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                var row = new StatusRow { InstanceId = sessions[i].InstanceId, Channel = ((i + 1).ToString("00")) };
                _statusRows.Add(row);
                _statusById[sessions[i].InstanceId] = row;
            }
        }

        int speaking = 0, ducking = 0;
        foreach (var s in sessions)
        {
            var row = _statusById[s.InstanceId];
            var e = states.GetValueOrDefault(s.InstanceId);
            row.PeakPercent = Math.Round(s.Peak * 100);
            row.StripOpacity = e?.Speaking == true ? 1.0 : 0.25;
            row.Process = s.ProcessName;
            row.PidText = s.Pid.ToString();
            row.GroupName = e?.GroupName ?? "—";
            row.GroupBrush = GroupBrush(e?.GroupId ?? "");
            row.SpeakingBrush = e?.Speaking == true ? GreenBrush : IdleBrush;
            if (e?.Speaking == true) speaking++;
            var volPos = (int)Math.Round(PosOf(s.Volume)); // 按全局曲线换算为推子位置，与音量页显示一致
            row.VolumePercent = volPos;
            row.VolumeText = $"{volPos}%";
            row.VolBrush = e is { Ducked: true } ? AmberBrush : AccentBrush;
            row.DuckVisibility = e is { Ducked: true } || e is { UserTookOver: true }
                ? Visibility.Visible : Visibility.Collapsed;
            row.DuckText = e is { Ducked: true }
                ? Lang.Get("S.Status.DuckBadge", Math.Round(PosOf(e?.TargetVolume ?? 0)))
                : Lang.Get("S.Status.TakeoverBadge");
            if (e is { Ducked: true }) ducking++;
            if (!ReferenceEquals(row.IconPngSource, s.IconPng))
            {
                row.IconPngSource = s.IconPng; // PNG 引用稳定（会话期间不变），换源才重新解码
                row.Icon = ProcessIconService.ToBitmap(s.IconPng);
            }
        }

        StatTotal.Text = sessions.Count.ToString();
        StatSpeaking.Text = speaking.ToString();
        StatDucking.Text = ducking.ToString();
        StatusEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
