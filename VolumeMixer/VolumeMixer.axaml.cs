using Avalonia;
using Avalonia.Controls;
using Docklys.ModuleContracts;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Avalonia.Threading;
using Newtonsoft.Json;
using Avalonia.Layout;
using Brushes = Avalonia.Media.Brushes;
using Color = Avalonia.Media.Color;

namespace VolumeMixer
{
    public partial class VolumeMixer : UserControl, IModule
    {
        public string Id => "VolumeMixer";
        public string ModuleName => "Volume Mixer";
        public string ModuleVersion => "1.0.0";
        public string Category => "QuickTools";
        public string[] Tags => new[] { "Volume", "Media", "Audio", "Music", "Mixer" };

        public int TileWidth => 1;
        public int TileHeight => 1;

        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "1.0.0";
        // Linux controls PulseAudio sink inputs through pactl. That API is also provided by
        // PipeWire's pipewire-pulse service, so the same implementation covers both stacks.
        public string[] SupportedPlatforms => new[] { "Windows", "Linux" };

        private static IAudioSessionBackend? AudioBackend => AudioSessionBackend.Current;
        private static bool AudioSessionsAvailable => AudioBackend is not null;

        private string? _uniqueModuleId;
        public string UniqueModuleId { get { return _uniqueModuleId ?? string.Empty; } }

        public void SetModuleId(string uniqueModuleId) { _uniqueModuleId = uniqueModuleId; }
        public void PrintModuleId() { Console.WriteLine($"Module ID: {UniqueModuleId}"); }

        public VolumeMixer()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ++_loadGeneration;
            _volumeUpdateTimer ??= new System.Threading.Timer(UpdateVolumesFromSessions, null, 1000, 500);
            UpdateAudioSessionIcons();
            GroupVolumeChanged += OnExternalGroupVolumeChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            GroupVolumeChanged -= OnExternalGroupVolumeChanged;
            _volumeUpdateTimer?.Dispose();
            _volumeUpdateTimer = null;
            foreach (var kvp in _sliderSessions)
                kvp.Value.slider.ValueChanged -= OnSliderValueChanged;
        }

        // Cross-instance peer link: when any VolumeMixer instance's slider moves,
        // all other live instances mirror the value on any slider that shares the
        // same groupKey. Runs synchronously on the UI thread so the visual update
        // is frame-perfect across modules.
        private static event Action<string, double, VolumeMixer>? GroupVolumeChanged;

        private void OnExternalGroupVolumeChanged(string groupKey, double newValue, VolumeMixer source)
        {
            if (ReferenceEquals(source, this)) return;
            _isProgrammaticUpdate = true;
            try
            {
                float newSystemVolume = (float)(newValue / 100.0);
                foreach (var kvp in _sliderSessions)
                {
                    if (kvp.Value.groupKey != groupKey) continue;
                    kvp.Value.slider.Value = newValue;
                    try { kvp.Value.session.SetVolume(newSystemVolume); } catch { }
                }
            }
            finally { _isProgrammaticUpdate = false; }
        }

        private Dictionary<string, (IAudioSession session, IImage? icon)> _buttonSessions = new();
        private Dictionary<string, bool> _buttonHasManualIcon = new();
        // groupKey is the stable identity for "same app" (process name, lower-cased).
        // Frozen at assignment time so it never drifts with tab-title changes.
        private Dictionary<string, (IAudioSession session, Slider slider, string groupKey)> _sliderSessions = new();
        private System.Threading.Timer? _volumeUpdateTimer;
        private int _loadGeneration = 0;
        private bool _isProgrammaticUpdate = false;

        private static string GetGroupKey(IAudioSession session) => session.GroupKey;

        private void PresetButton_Click(object? sender, RoutedEventArgs e)
        {
            var clickedButton = sender as Button;
            if (clickedButton == null) return;
            // The picker's whole content is the session list, so there is nothing to show here
            // without one. Bail before building the popup rather than opening an empty one.
            if (!AudioSessionsAvailable) return;

            // Match the 110×110 tile exactly. The old 100px popup plus manual offsets left a
            // visible strip of the mixer around the picker on some Linux window managers.
            const double fixedPopupSize = 110;
            var popup = new Popup
            {
                PlacementMode = PlacementMode.Center,
                PlacementTarget = this,
                IsLightDismissEnabled = true,
                Width = fixedPopupSize,
                Height = fixedPopupSize
            };

            var container = new Border
            {
                Background = GetAppBrush("ColorModuleColor", Color.FromArgb(255, 28, 28, 30)),
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(6),
                Width = fixedPopupSize,
                Height = fixedPopupSize
            };

            var sessions = AudioBackend!.GetSessions();

            var validSessions = new List<(IAudioSession session, string name, IImage? icon)>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (IsDisallowedSession(session)) continue;
                string name = GetSessionDisplayName(session);
                var icon = GetIconForSession(session);
                validSessions.Add((session, name, icon));
            }

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*") };
            var itemsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Margin = new Avalonia.Thickness(0)
            };

            if (validSessions.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "No active audio sessions",
                    Foreground = GetAppBrush("ColorModuleFont", Color.FromArgb(255, 142, 142, 147)),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(0, 8),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };
                itemsPanel.Children.Add(emptyText);
            }
            else
            {
                double spacing = itemsPanel.Spacing;
                double availableHeight = container.Height - (container.Padding.Top + container.Padding.Bottom);
                if (double.IsNaN(availableHeight) || availableHeight <= 0)
                    availableHeight = 102;

                double buttonHeight = Math.Max(20.0, (availableHeight - (spacing * (validSessions.Count - 1))) / validSessions.Count);
                Grid.SetRow(itemsPanel, 1);

                foreach (var (session, name, icon) in validSessions)
                {
                    var sessionButton = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Avalonia.Thickness(4, 0),
                        Background = new SolidColorBrush(Color.FromArgb(255, 44, 44, 46)),
                        BorderThickness = new Avalonia.Thickness(0),
                        CornerRadius = new Avalonia.CornerRadius(4),
                        Cursor = new Cursor(StandardCursorType.Hand),
                        Height = buttonHeight,
                        Margin = new Avalonia.Thickness(0)
                    };

                    var contentPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    if (icon != null)
                    {
                        var iconImage = new Avalonia.Controls.Image
                        {
                            Source = icon,
                            Width = 12,
                            Height = 12,
                            Stretch = Avalonia.Media.Stretch.Uniform,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        contentPanel.Children.Add(iconImage);
                    }

                    var textBlock = new TextBlock
                    {
                        Text = name,
                        Foreground = GetAppBrush("ColorModuleFont", Color.FromArgb(255, 255, 255, 255)),
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                    };
                    contentPanel.Children.Add(textBlock);

                    sessionButton.Content = contentPanel;
                    sessionButton.Foreground = GetAppBrush("ColorModuleFont", Color.FromArgb(255, 255, 255, 255));

                    sessionButton.Click += (s, args) =>
                    {
                        var buttonName = clickedButton.Name ?? "";

                        // Linux does not have Windows shell icons, but an absent icon must never
                        // prevent assigning the selected application to this slider.
                        SetIconToButton(clickedButton, icon, name);
                        _buttonHasManualIcon[buttonName] = true;
                        _buttonSessions[buttonName] = (session, icon);

                        var sliderName = buttonName.Replace("SourceIcon", "VolumeSlider");
                        var slider = this.FindControl<Slider>(sliderName);

                        if (slider != null)
                        {
                            // Bulletproof reassignment: physically detach handler,
                            // swap mapping FIRST, then set value, then reattach.
                            // No event raised during the swap can write the old
                            // slider value back to either session.
                            slider.ValueChanged -= OnSliderValueChanged;
                            _sliderSessions[sliderName] = (session, slider, GetGroupKey(session));

                            _isProgrammaticUpdate = true;
                            try { slider.Value = session.Volume * 100; } catch { }
                            _isProgrammaticUpdate = false;

                            slider.ValueChanged += OnSliderValueChanged;
                        }

                        UpdateSliderSource(sliderName, name);

                        popup.Close();
                    };

                    sessionButton.PointerEntered += (s, args) =>
                    {
                        sessionButton.Background = new SolidColorBrush(Color.FromArgb(255, 58, 58, 60));
                    };

                    sessionButton.PointerExited += (s, args) =>
                    {
                        sessionButton.Background = new SolidColorBrush(Color.FromArgb(255, 44, 44, 46));
                    };

                    itemsPanel.Children.Add(sessionButton);
                }
            }

            grid.Children.Add(itemsPanel);
            container.Child = grid;
            popup.Child = container;

            popup.Opened += (s, e) =>
            {
                if (popup.Host is Panel panel)
                {
                    panel.Background = Brushes.Transparent;
                }
            };

            popup.Open();
        }

        private MenuItem CreateMenuItem(string header)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (s, e) => OnPresetSelected(header);
            menuItem.PointerPressed += async (sender, e) =>
            {
                if (e.GetCurrentPoint(menuItem).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
                {
                    e.Handled = true;
                    await Task.Delay(100);
                    StartRename(menuItem);
                }
            };
            return menuItem;
        }

        private void StartRename(MenuItem menuItem)
        {
            var currentText = menuItem.Header?.ToString() ?? "";
            if (currentText == "Custom...") return;

            var textBox = new TextBox
            {
                Text = currentText,
                Classes = { "inline-edit" },
                MaxLength = 8
            };

            menuItem.Header = textBox;
            textBox.Focus();
            textBox.SelectAll();

            textBox.LostFocus += (s, e) => FinishRename(menuItem, textBox);
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) FinishRename(menuItem, textBox);
                else if (e.Key == Key.Escape) menuItem.Header = currentText;
            };
        }

        private void FinishRename(MenuItem menuItem, TextBox textBox)
        {
            var newText = textBox.Text?.Trim();
            if (string.IsNullOrEmpty(newText)) newText = "Preset";
            menuItem.Header = newText;
        }

        private void OnPresetSelected(string preset) { }

        private IImage? GetIconForSession(IAudioSession session)
        {
            // System.Drawing icon extraction is a Windows shell feature. Linux applications
            // use freedesktop.org icon themes instead, then fall back to an app initial.
            if (OperatingSystem.IsLinux()) return LinuxIconResolver.Load(session);
            if (!OperatingSystem.IsWindows()) return null;
            return GetWindowsIconForSession(session);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private IImage? GetWindowsIconForSession(IAudioSession session)
        {
            try
            {
                if (session.ProcessId is not int processId) return null;
                var process = Process.GetProcessById(processId);
                if (process.MainModule?.FileName == null) return null;

                var icon = Icon.ExtractAssociatedIcon(process.MainModule.FileName);
                if (icon == null) return null;

                using (var originalBitmap = icon.ToBitmap())
                {
                    var processedBitmap = ApplyMonochrome(originalBitmap);
                    using (var stream = new MemoryStream())
                    {
                        processedBitmap.Save(stream, ImageFormat.Png);
                        stream.Seek(0, SeekOrigin.Begin);
                        var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                        processedBitmap.Dispose();
                        return avaloniaBitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG] Exception in GetIconForSession: {ex.Message}");
            }
            return null;
        }

        // Turns any app icon into a punchy full-range black/white glyph on a transparent
        // background, matching the Linux ImageMagick filter in LinuxIconResolver so both
        // platforms render identically. The original alpha (and therefore the icon's shape
        // and soft edges) is preserved untouched; only the colour is replaced. The tone map
        // is: auto-level (stretch the icon's own grey range to full 0..255) then a strong
        // sigmoidal S-curve that snaps tones toward crisp black and white while keeping
        // internal detail, with a bright-biased midpoint so it reads white-leaning on the
        // medium-grey tile without washing out to a flat grey blob.
        private const double SigmoidContrast = 7.0;
        private const double SigmoidMidpoint = 0.40;

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private System.Drawing.Bitmap ApplyMonochrome(System.Drawing.Bitmap original)
        {
            var processed = new System.Drawing.Bitmap(original.Width, original.Height);

            // First pass: find the icon's own min/max luminance (over visible pixels) so we
            // can stretch it across the full range, exactly like ImageMagick's -auto-level.
            int min = 255, max = 0;
            for (int x = 0; x < original.Width; x++)
            {
                for (int y = 0; y < original.Height; y++)
                {
                    var pixel = original.GetPixel(x, y);
                    if (pixel.A == 0) continue;
                    int gray = (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                    if (gray < min) min = gray;
                    if (gray > max) max = gray;
                }
            }
            double range = max > min ? max - min : 0;

            // Precompute the sigmoidal normalization endpoints once.
            double f0 = Sigmoid(0.0), f1 = Sigmoid(1.0);
            double fSpan = f1 - f0;

            for (int x = 0; x < original.Width; x++)
            {
                for (int y = 0; y < original.Height; y++)
                {
                    var pixel = original.GetPixel(x, y);
                    if (pixel.A == 0)
                    {
                        processed.SetPixel(x, y, pixel);
                        continue;
                    }

                    int gray = (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                    // auto-level: stretch to 0..1. Flat icons (no range) bias bright.
                    double leveled = range > 0 ? (gray - min) / range : 1.0;
                    // strong S-curve toward black/white, bright-biased midpoint.
                    double curved = fSpan > 0 ? (Sigmoid(leveled) - f0) / fSpan : leveled;
                    int tone = Math.Clamp((int)Math.Round(curved * 255.0), 0, 255);
                    processed.SetPixel(x, y, System.Drawing.Color.FromArgb(pixel.A, tone, tone, tone));
                }
            }
            return processed;
        }

        // ImageMagick-compatible increasing sigmoidal contrast transfer for x in [0,1].
        private static double Sigmoid(double x) =>
            1.0 / (1.0 + Math.Exp(SigmoidContrast * (SigmoidMidpoint - x)));

        private void SetIconToButton(Button button, IImage? icon, string? fallbackText = null)
        {
            if (icon == null)
            {
                var label = string.IsNullOrWhiteSpace(fallbackText)
                    ? "≡"
                    : fallbackText.Trim()[0].ToString().ToUpperInvariant();
                button.Content = label;
                ToolTip.SetTip(button, fallbackText);
                return;
            }

            var image = new Avalonia.Controls.Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Stretch = Avalonia.Media.Stretch.Uniform
            };
            button.Content = image;
            ToolTip.SetTip(button, fallbackText);
        }

        private void UpdateAudioSessionIcons()
        {
            var buttons = new[] {
                this.FindControl<Button>("SourceIcon1"),
                this.FindControl<Button>("SourceIcon2"),
                this.FindControl<Button>("SourceIcon3"),
                this.FindControl<Button>("SourceIcon4"),
            };
            var sliders = new[] {
                this.FindControl<Slider>("VolumeSlider1"),
                this.FindControl<Slider>("VolumeSlider2"),
                this.FindControl<Slider>("VolumeSlider3"),
                this.FindControl<Slider>("VolumeSlider4"),
            };

            // No supported backend is available on macOS, but the tile can still render safely.
            if (!AudioSessionsAvailable) return;

            var sessions = AudioBackend!.GetSessions();

            var buttonKeysToRemove = new List<string>();
            foreach (var kv in _buttonSessions)
            {
                try
                {
                    var session = kv.Value.session;
                    if (IsDisallowedSession(session) || IsDisallowedSessionName(GetSessionDisplayName(session)))
                        buttonKeysToRemove.Add(kv.Key);
                }
                catch { }
            }
            foreach (var k in buttonKeysToRemove) _buttonSessions.Remove(k);

            var sliderKeysToRemove = new List<string>();
            foreach (var kv in _sliderSessions)
            {
                try
                {
                    var session = kv.Value.session;
                    if (IsDisallowedSession(session) || IsDisallowedSessionName(GetSessionDisplayName(session)))
                        sliderKeysToRemove.Add(kv.Key);
                }
                catch { }
            }
            foreach (var k in sliderKeysToRemove) _sliderSessions.Remove(k);

            var filteredSessions = new List<IAudioSession>();
            for (int si = 0; si < sessions.Count; si++)
            {
                var s = sessions[si];
                if (IsDisallowedSession(s)) continue;
                filteredSessions.Add(s);
            }

            for (int i = 0; i < sliders.Length; i++)
            {
                var slider = sliders[i];
                var sliderName = slider?.Name ?? $"Slider{i}";
                if (slider != null) UpdateSliderFromJson(slider, sliderName);
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                var slider = sliders[i];
                var buttonName = button?.Name ?? $"Button{i}";
                var sliderName = slider?.Name ?? $"Slider{i}";

                if (button != null && slider != null)
                {
                    if (_buttonHasManualIcon.ContainsKey(buttonName) && _buttonHasManualIcon[buttonName])
                    {
                        if (_buttonSessions.ContainsKey(buttonName))
                        {
                            var (manualSession, manualIcon) = _buttonSessions[buttonName];
                            SetIconToButton(button, manualIcon, GetSessionDisplayName(manualSession));
                            slider.ValueChanged -= OnSliderValueChanged;
                            _sliderSessions[sliderName] = (manualSession, slider, GetGroupKey(manualSession));
                            _isProgrammaticUpdate = true;
                            try { slider.Value = manualSession.Volume * 100; } catch { }
                            _isProgrammaticUpdate = false;
                            slider.ValueChanged += OnSliderValueChanged;
                        }
                        continue;
                    }

                    if (_sliderSessions.ContainsKey(sliderName))
                    {
                        var jsonSession = _sliderSessions[sliderName].session;
                        var icon = GetIconForSession(jsonSession);
                        SetIconToButton(button, icon, GetSessionDisplayName(jsonSession));
                        slider.ValueChanged -= OnSliderValueChanged;
                        slider.ValueChanged += OnSliderValueChanged;
                        continue;
                    }

                    if (i < filteredSessions.Count)
                    {
                        var session = filteredSessions[i];
                        var icon = GetIconForSession(session);
                        SetIconToButton(button, icon, GetSessionDisplayName(session));

                        slider.ValueChanged -= OnSliderValueChanged;
                        _sliderSessions[sliderName] = (session, slider, GetGroupKey(session));
                        _isProgrammaticUpdate = true;
                        try { slider.Value = session.Volume * 100; } catch { }
                        _isProgrammaticUpdate = false;
                        slider.ValueChanged += OnSliderValueChanged;
                    }
                    else
                    {
                        SetIconToButton(button, null);
                        if (_sliderSessions.ContainsKey(sliderName))
                            _sliderSessions.Remove(sliderName);
                    }
                }
            }
        }

        private void OnSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticUpdate) return;
            if (sender is not Slider slider) return;

            var sliderName = slider.Name ?? "Unknown";
            if (!_sliderSessions.ContainsKey(sliderName)) return;

            var entry = _sliderSessions[sliderName];
            var session = entry.session;
            var myGroupKey = entry.groupKey;

            try
            {
                float newSystemVolume = (float)(e.NewValue / 100.0);
                session.SetVolume(newSystemVolume);

                _isProgrammaticUpdate = true;
                foreach (var kvp in _sliderSessions)
                {
                    if (kvp.Key == sliderName) continue;
                    if (kvp.Value.groupKey != myGroupKey) continue;
                    kvp.Value.slider.Value = e.NewValue;
                    try { kvp.Value.session.SetVolume(newSystemVolume); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG] OnSliderValueChanged error: {ex.Message}");
            }
            finally
            {
                _isProgrammaticUpdate = false;
            }

            // Broadcast to all other VolumeMixer instances so their matching
            // sliders mirror the change instantly (no timer round-trip).
            try { GroupVolumeChanged?.Invoke(myGroupKey, e.NewValue, this); } catch { }
        }

        private void UpdateVolumesFromSessions(object? state)
        {
            int generation = _loadGeneration;
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _loadGeneration) return;

                // pactl's JSON response is the authoritative Linux state. Sink-input objects are
                // snapshots, so refresh them once per tick before reading the slider values.
                Dictionary<string, IAudioSession>? currentLinuxSessions = null;
                if (OperatingSystem.IsLinux() && AudioBackend is not null)
                {
                    try { currentLinuxSessions = AudioBackend.GetSessions().ToDictionary(session => session.Id); }
                    catch (Exception ex) { Debug.WriteLine($"[DEBUG] Linux session refresh failed: {ex.Message}"); }
                }

                var staleKeys = new List<string>();
                var refreshedSessions = new List<(string sliderName, IAudioSession session, Slider slider)>();
                foreach (var kvp in _sliderSessions)
                {
                    try
                    {
                        var session = kvp.Value.session;
                        var slider = kvp.Value.slider;

                        if (currentLinuxSessions is not null)
                        {
                            if (!currentLinuxSessions.TryGetValue(session.Id, out session))
                            {
                                staleKeys.Add(kvp.Key);
                                continue;
                            }
                            refreshedSessions.Add((kvp.Key, session, slider));
                        }

                        float systemVolume = session.Volume;
                        double currentSliderValue = slider.Value;
                        double expectedSliderValue = systemVolume * 100;
                        if (Math.Abs(currentSliderValue - expectedSliderValue) > 1)
                        {
                            _isProgrammaticUpdate = true;
                            slider.Value = expectedSliderValue;
                            _isProgrammaticUpdate = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DEBUG] Stale session for {kvp.Key}, removing: {ex.Message}");
                        staleKeys.Add(kvp.Key);
                    }
                }
                foreach (var k in staleKeys) _sliderSessions.Remove(k);
                foreach (var (sliderName, session, slider) in refreshedSessions)
                    _sliderSessions[sliderName] = (session, slider, GetGroupKey(session));
            });
        }

        private static string GetSessionDisplayName(IAudioSession session) => session.DisplayName;

        private string GetJsonFilePath()
        {
            string moduleId = string.IsNullOrEmpty(UniqueModuleId) ? "NoIdSet" : UniqueModuleId;
            string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "ModuleSaves", "VolumeMixer");
            Directory.CreateDirectory(appDataPath);
            return Path.Combine(appDataPath, $"VolumeMixer_{moduleId}.json");
        }

        public void SaveSliderPaths(Dictionary<string, string> sliderPaths)
        {
            string filePath = GetJsonFilePath();
            string jsonContent = JsonConvert.SerializeObject(sliderPaths, Formatting.Indented);
            try { File.WriteAllText(filePath, jsonContent); }
            catch (Exception ex) { Debug.WriteLine($"[DEBUG] Save error: {ex.Message}"); }
        }

        public Dictionary<string, string> LoadSliderPaths()
        {
            string filePath = GetJsonFilePath();
            if (!File.Exists(filePath)) return new Dictionary<string, string>();

            try
            {
                string jsonContent = File.ReadAllText(filePath);
                var sliderPaths = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonContent)
                                  ?? new Dictionary<string, string>();

                var keysToRemove = new List<string>();
                foreach (var kv in sliderPaths)
                {
                    var storedName = kv.Value ?? string.Empty;
                    if (IsDisallowedSessionName(storedName)) keysToRemove.Add(kv.Key);
                }
                foreach (var k in keysToRemove) sliderPaths.Remove(k);
                return sliderPaths;
            }
            catch { return new Dictionary<string, string>(); }
        }

        private bool IsDisallowedSessionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            if (n.Equals("SystemSounds", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("System Sounds", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.IndexOf("systemroot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void UpdateSliderSource(string sliderName, string sourceName)
        {
            var sliderPaths = LoadSliderPaths();
            sliderPaths[sliderName] = sourceName;
            SaveSliderPaths(sliderPaths);
        }

        private void UpdateSliderFromJson(Slider slider, string sliderName)
        {
            if (_sliderSessions.ContainsKey(sliderName)) return;
            if (!AudioSessionsAvailable) return;

            var sessions = AudioBackend!.GetSessions();

            var sliderPaths = LoadSliderPaths();
            if (!sliderPaths.ContainsKey(sliderName)) return;

            var targetSessionName = sliderPaths[sliderName];
            IAudioSession? sessionFromJson = null;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (IsDisallowedSession(session)) continue;
                if (GetSessionDisplayName(session) == targetSessionName)
                {
                    sessionFromJson = session;
                    break;
                }
            }

            if (sessionFromJson != null)
            {
                float sessionVolume = sessionFromJson.Volume;
                double newSliderValue = sessionVolume * 100;

                slider.ValueChanged -= OnSliderValueChanged;
                _sliderSessions[sliderName] = (sessionFromJson, slider, GetGroupKey(sessionFromJson));
                _isProgrammaticUpdate = true;
                try { slider.Value = newSliderValue; } catch { }
                _isProgrammaticUpdate = false;
                slider.ValueChanged += OnSliderValueChanged;
            }
        }

        private bool IsDisallowedSession(IAudioSession session)
        {
            string name = session.DisplayName;

            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();

            if (n.Equals("SystemSounds", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("System Sounds", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.IndexOf("systemroot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private IBrush GetAppBrush(string resourceKey, Color fallback)
        {
            try
            {
                var app = Application.Current;
                if (app?.Resources?.ContainsKey(resourceKey) == true)
                {
                    var val = app.Resources[resourceKey];
                    if (val is IBrush ib) return ib;
                    if (val is SolidColorBrush sb) return sb;
                }
            }
            catch { }
            return new SolidColorBrush(fallback);
        }
    }
}
