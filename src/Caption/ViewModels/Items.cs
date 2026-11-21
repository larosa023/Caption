using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using Caption.Audio;
using Caption.Services;
using Caption.Whisper;

namespace Caption.ViewModels
{
    /// <summary>A profile in the list, editable in place. Every change saves and applies right away.</summary>
    public sealed class ProfileItem : ObservableObject
    {
        private readonly Action<ProfileItem, bool> _changed;
        private readonly Func<string, DeviceChoice?> _findOutput;
        private readonly Func<string, DeviceChoice?> _findInput;
        private bool _isActive;

        public ProfileItem(Profile profile, Action<ProfileItem, bool> changed,
            Func<string, DeviceChoice?> findOutput, Func<string, DeviceChoice?> findInput)
        {
            Profile = profile;
            _changed = changed;
            _findOutput = findOutput;
            _findInput = findInput;
            Apps = new ObservableCollection<string>(profile.Apps);
        }

        public Profile Profile { get; }
        public string Id => Profile.Id;
        public ObservableCollection<string> Apps { get; }

        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

        public string Name
        {
            get => Profile.Name;
            set { if (!string.IsNullOrWhiteSpace(value) && value != Profile.Name) { Profile.Name = value.Trim(); OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(this, false); } }
        }

        public string Source
        {
            get => Profile.Source;
            set { if (value != Profile.Source) { Profile.Source = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(this, true); } }
        }

        public DeviceChoice? Device
        {
            get => _findOutput(Profile.DeviceId);
            set
            {
                if (value == null || value.Id == Profile.DeviceId) return;
                Profile.DeviceId = value.Id;
                Profile.DeviceName = value.Name;
                OnPropertyChanged(nameof(DeviceName));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged();
                _changed(this, true);
            }
        }

        public string DeviceName => string.IsNullOrEmpty(Profile.DeviceName) ? "Choose an output" : Profile.DeviceName;

        public bool IncludeMic
        {
            get => Profile.IncludeMic;
            set { if (value != Profile.IncludeMic) { Profile.IncludeMic = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(this, true); } }
        }

        public DeviceChoice? Mic
        {
            get => _findInput(Profile.MicDeviceId);
            set
            {
                if (value == null || value.Id == Profile.MicDeviceId) return;
                Profile.MicDeviceId = value.Id;
                Profile.MicDeviceName = value.Id.Length == 0 ? "" : value.Name;
                OnPropertyChanged(nameof(MicName));
                OnPropertyChanged();
                _changed(this, true);
            }
        }

        public string MicName => string.IsNullOrEmpty(Profile.MicDeviceName) ? "Default microphone" : Profile.MicDeviceName;

        public string Language
        {
            get => Profile.Language;
            set { if (value != null && value != Profile.Language) { Profile.Language = value; OnPropertyChanged(); _changed(this, false); } }
        }

        public string Translate
        {
            get => Profile.Translate;
            set { if (value != Profile.Translate) { Profile.Translate = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(this, false); } }
        }

        public string TargetLanguage
        {
            get => Profile.TargetLanguage;
            set { if (!string.IsNullOrWhiteSpace(value) && value != Profile.TargetLanguage) { Profile.TargetLanguage = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(this, false); } }
        }

        public bool ShowOriginal
        {
            get => Profile.ShowOriginal;
            set { if (value != Profile.ShowOriginal) { Profile.ShowOriginal = value; OnPropertyChanged(); _changed(this, false); } }
        }

        /// <summary>Device lists were refreshed: re-read the selections.</summary>
        public void RefreshDevices()
        {
            OnPropertyChanged(nameof(Device));
            OnPropertyChanged(nameof(Mic));
        }

        public void AddApp(string exe)
        {
            exe = exe.Trim();
            if (exe.Length == 0) return;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            if (Apps.Any(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase))) return;
            Apps.Add(exe);
            Profile.Apps = Apps.ToList();
            OnPropertyChanged(nameof(Summary));
            _changed(this, true);
        }

        public void RemoveApp(string exe)
        {
            Apps.Remove(exe);
            Profile.Apps = Apps.ToList();
            OnPropertyChanged(nameof(Summary));
            _changed(this, true);
        }

        public string Summary
        {
            get
            {
                string src = Profile.Source switch
                {
                    "device" => DeviceName,
                    "apps" => Profile.Apps.Count == 0 ? "No apps yet" : string.Join(", ", Profile.Apps.Select(a => System.IO.Path.GetFileNameWithoutExtension(a)).Take(3)) + (Profile.Apps.Count > 3 ? $" +{Profile.Apps.Count - 3}" : ""),
                    _ => "Everything on the PC",
                };
                if (Profile.IncludeMic) src += " + mic";
                string tr = Profile.Translate switch
                {
                    "english" => " · to English",
                    "ollama" => " · to " + Profile.TargetLanguage,
                    _ => "",
                };
                return src + tr;
            }
        }
    }

    public sealed class ModelItem : ObservableObject
    {
        private bool _isActive;
        private bool _isDownloading;
        private double _progress;
        private string _progressText = "";

        public ModelItem(ModelInfo info) => Info = info;

        public ModelInfo Info { get; }
        public string Name => Info.Name;
        public string Blurb => Info.Blurb;
        public string SizeText => Info.SizeText;
        public int Speed => Info.Speed;
        public int Accuracy => Info.Accuracy;
        public bool Recommended => Info.Recommended;
        public bool IsInstalled => Info.IsInstalled;
        public string SpeedText(bool gpu) => gpu ? Info.GpuSpeed : Info.CpuSpeed;
        public string SpeedLabel { get; set; } = "";

        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
        public bool IsDownloading { get => _isDownloading; set => SetProperty(ref _isDownloading, value); }
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
        public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

        public ICommand? DownloadCommand { get; set; }
        public ICommand? UseCommand { get; set; }
        public ICommand? DeleteCommand { get; set; }
        public ICommand? CancelCommand { get; set; }

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsInstalled));
        }
    }

    public sealed class ThemeItem : ObservableObject
    {
        private bool _isSelected;

        public ThemeItem(ThemeDef def)
        {
            Def = def;
            var shown = def.FollowWindows ? ThemeManager.BuiltinThemes[ThemeManager.SystemUsesLightTheme() ? 1 : 0] : def;
            Background = ThemeManager.Brush(ThemeManager.Rgb(shown.Bg));
            Surface = ThemeManager.Brush(ThemeManager.Rgb(shown.Surface == 0xFFFFFF && shown.SurfaceOpacity < 1 ? 0x2A2A31 : shown.Surface));
            Text = ThemeManager.Brush(ThemeManager.Rgb(shown.Text));
            Accent = ThemeManager.Brush(ThemeManager.Rgb(shown.Accent != 0 ? shown.Accent : ThemeManager.SystemAccent()));
        }

        public ThemeDef Def { get; }
        public string Id => Def.Id;
        public string Name => Def.Name;
        public string Description => Def.Description;
        public Brush Background { get; }
        public Brush Surface { get; }
        public Brush Text { get; }
        public Brush Accent { get; }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    public sealed class SwatchItem : ObservableObject
    {
        private bool _isSelected;
        public SwatchItem(string value, string label, Brush brush) { Value = value; Label = label; Brush = brush; }
        public string Value { get; }
        public string Label { get; }
        public Brush Brush { get; }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    public sealed class Choice
    {
        public Choice(string value, string label) { Value = value; Label = label; }
        public string Value { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }
}
