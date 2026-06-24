using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Docklys.ModuleContracts;

namespace Comms
{
    // The Comms module is a protocol-agnostic chat shell. It never references
    // Matrix (or any protocol) — it discovers providers through CommsBridge and
    // talks to each only through ICommsProvider. Installing/uninstalling a
    // provider plugin at runtime adds/removes a protocol live via ProvidersChanged.
    public partial class Comms : UserControl, IModule, IResizable
    {
        // Identification
        public string Id => "Comms";
        public string ModuleName => "Comms";
        public string ModuleVersion => "1.0.0";
        public string Category => "Default";
        public string[] Tags => new[] { "comms", "chat", "matrix", "messaging" };

        // Layout — 3x3 by default, but resizable (see IResizable).
        public int TileWidth => 3;
        public int TileHeight => 3;
        public int PreferredTileWidth => 3;
        public int PreferredTileHeight => 3;

        // Compatibility
        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "2.0.0";
        public string[] SupportedPlatforms => new[] { "Windows", "Linux", "Mac" };

        // Unique Module ID (set by the main app)
        private string _uniqueModuleId = string.Empty;
        public string UniqueModuleId => _uniqueModuleId;
        public void SetModuleId(string uniqueModuleId) => _uniqueModuleId = uniqueModuleId;
        public void PrintModuleId() => Console.WriteLine($"Module ID: {UniqueModuleId}");

        // IResizable
        public event Action<int, int>? TileResizeRequested;
        public void SetTileSize(int width, int height)
        {
            _currentW = Math.Max(1, width);
            _currentH = Math.Max(1, height);
            ApplySize();
        }

        // Tile geometry (matches the dock's ~115x117 px cell, from the original 2x3=230x350).
        private const double CellW = 115;
        private const double CellH = 117;
        private const double SidebarWidth = 150;
        private const double NarrowThreshold = 300;

        private int _currentW = 3;
        private int _currentH = 3;
        private bool _narrow;
        private bool _drawerOpen;

        // Selection
        private ICommsProvider? _activeProvider;
        private string? _activeContactId;

        // Subscriptions
        private IDisposable? _boundsSub;
        private readonly List<ICommsProvider> _wired = new();
        private readonly Dictionary<ICommsProvider, Action<CommsMessage>> _msgHandlers = new();
        private readonly Dictionary<ICommsProvider, Action> _changedHandlers = new();
        private int _rebuildSeq;

        public Comms()
        {
            InitializeComponent();

            WidthMinus.Click  += (_, _) => Adjust(-1, 0);
            WidthPlus.Click   += (_, _) => Adjust(+1, 0);
            HeightMinus.Click += (_, _) => Adjust(0, -1);
            HeightPlus.Click  += (_, _) => Adjust(0, +1);

            MenuButton.Click  += (_, _) => SetDrawer(!_drawerOpen);
            Scrim.PointerPressed += (_, _) => SetDrawer(false);

            SendButton.Click  += async (_, _) => await SendCurrent();
            Composer.KeyDown  += async (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; await SendCurrent(); }
            };
            AttachButton.Click += async (_, _) => await AttachFile();

            ApplySize();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _boundsSub = this.GetObservable(BoundsProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(r => EvaluateResponsive(r.Width)));

            CommsBridge.ProvidersChanged += OnProvidersChanged;
            WireProviders();
            UpdateStatus();
            _ = RebuildContactsAsync();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _boundsSub?.Dispose();
            _boundsSub = null;
            CommsBridge.ProvidersChanged -= OnProvidersChanged;
            UnwireProviders();
        }

        // ── Provider wiring ─────────────────────────────────────────────────────
        private void OnProvidersChanged() => Dispatcher.UIThread.Post(() =>
        {
            UnwireProviders();
            WireProviders();
            UpdateStatus();
            _ = RebuildContactsAsync();
        });

        private void WireProviders()
        {
            foreach (var p in CommsBridge.Providers)
            {
                Action<CommsMessage> mh = m => Dispatcher.UIThread.Post(() => OnMessageReceived(p, m));
                Action ch = () => Dispatcher.UIThread.Post(() => { UpdateStatus(); ScheduleContactsRebuild(); });
                p.MessageReceived += mh;
                p.Changed += ch;
                _msgHandlers[p] = mh;
                _changedHandlers[p] = ch;
                _wired.Add(p);
            }
        }

        private void UnwireProviders()
        {
            foreach (var p in _wired)
            {
                if (_msgHandlers.TryGetValue(p, out var mh)) p.MessageReceived -= mh;
                if (_changedHandlers.TryGetValue(p, out var ch)) p.Changed -= ch;
            }
            _wired.Clear();
            _msgHandlers.Clear();
            _changedHandlers.Clear();
        }

        private void OnMessageReceived(ICommsProvider provider, CommsMessage m)
        {
            if (ReferenceEquals(provider, _activeProvider) && m.ContactId == _activeContactId)
            {
                AddBubble(m);
                ScrollToEnd();
            }
            else
            {
                ScheduleContactsRebuild(); // refresh unread counts
            }
        }

        // ── Contact list ─────────────────────────────────────────────────────────
        private bool _rebuildScheduled;
        private void ScheduleContactsRebuild()
        {
            if (_rebuildScheduled) return;
            _rebuildScheduled = true;
            Dispatcher.UIThread.Post(async () =>
            {
                _rebuildScheduled = false;
                await RebuildContactsAsync();
            }, DispatcherPriority.Background);
        }

        private async System.Threading.Tasks.Task RebuildContactsAsync()
        {
            var seq = ++_rebuildSeq;
            var providers = CommsBridge.Providers;

            // Gather off the UI mutation path; each GetContactsAsync may await network.
            var sections = new List<(ICommsProvider provider, IReadOnlyList<CommsContact> contacts)>();
            foreach (var p in providers)
            {
                IReadOnlyList<CommsContact> contacts;
                try { contacts = await p.GetContactsAsync(); }
                catch { contacts = Array.Empty<CommsContact>(); }
                if (seq != _rebuildSeq) return; // superseded
                sections.Add((p, contacts));
            }

            ContactList.Children.Clear();
            if (providers.Count == 0)
            {
                ContactList.Children.Add(Muted("No providers.\nInstall a Comms plugin\n(e.g. Matrix) in Settings ▸ Plugins."));
                return;
            }

            foreach (var (provider, contacts) in sections)
            {
                ContactList.Children.Add(new TextBlock
                {
                    Text = provider.ProviderName,
                    FontSize = 9, FontWeight = FontWeight.Bold, Opacity = 0.6,
                    Margin = new Thickness(4, 6, 4, 2), Foreground = FontBrush(),
                });

                if (contacts.Count == 0)
                {
                    ContactList.Children.Add(Muted(provider.IsConnected ? "No chats yet." : "Not connected."));
                    continue;
                }

                foreach (var c in contacts)
                    ContactList.Children.Add(BuildContactRow(provider, c));
            }
        }

        private Control BuildContactRow(ICommsProvider provider, CommsContact c)
        {
            var title = new TextBlock
            {
                Text = c.DisplayName, FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = FontBrush(), VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new DockPanel { LastChildFill = true };
            if (c.UnreadCount > 0)
            {
                var badge = new Border
                {
                    Background = AccentBrush(), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 0), Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = c.UnreadCount.ToString(), FontSize = 9, Foreground = Brushes.White },
                };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
            }
            row.Children.Add(title);

            var btn = new Button
            {
                Content = row, Background = Brushes.Transparent,
                Padding = new Thickness(6, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
            };
            bool active = ReferenceEquals(provider, _activeProvider) && c.Id == _activeContactId;
            if (active) btn.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            btn.Click += async (_, _) => await SelectContact(provider, c);
            return btn;
        }

        private async System.Threading.Tasks.Task SelectContact(ICommsProvider provider, CommsContact c)
        {
            _activeProvider = provider;
            _activeContactId = c.Id;
            ConvTitle.Text = c.DisplayName;
            if (_narrow) SetDrawer(false);

            MessageList.Children.Clear();
            MessageList.Children.Add(Muted("Loading…"));

            IReadOnlyList<CommsMessage> msgs;
            try { msgs = await provider.GetMessagesAsync(c.Id, 50); }
            catch { msgs = Array.Empty<CommsMessage>(); }

            // Ignore if the user switched conversations while loading.
            if (!ReferenceEquals(provider, _activeProvider) || c.Id != _activeContactId) return;

            MessageList.Children.Clear();
            if (msgs.Count == 0) MessageList.Children.Add(Muted("No messages yet."));
            else foreach (var m in msgs) AddBubble(m);
            ScrollToEnd();
            ScheduleContactsRebuild(); // clear unread highlight
        }

        // ── Sending ───────────────────────────────────────────────────────────────
        private async System.Threading.Tasks.Task SendCurrent()
        {
            var text = Composer.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _activeProvider == null || _activeContactId == null) return;
            Composer.Text = "";
            try { await _activeProvider.SendMessageAsync(_activeContactId, text); }
            catch (Exception ex) { MessageList.Children.Add(Muted("Send failed: " + ex.Message)); }
        }

        private async System.Threading.Tasks.Task AttachFile()
        {
            if (_activeProvider == null || _activeContactId == null) return;
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            var file = files.FirstOrDefault();
            if (file == null) return;

            try
            {
                await using var stream = await file.OpenReadAsync();
                await _activeProvider.SendFileAsync(_activeContactId, file.Name, GuessMime(file.Name), stream);
            }
            catch (Exception ex) { MessageList.Children.Add(Muted("Attach failed: " + ex.Message)); }
        }

        // ── Message bubbles ────────────────────────────────────────────────────────
        private void AddBubble(CommsMessage m)
        {
            var outgoing = m.Direction == MessageDirection.Outgoing;
            var stack = new StackPanel { Spacing = 1 };

            if (!outgoing)
                stack.Children.Add(new TextBlock
                {
                    Text = m.SenderDisplayName, FontSize = 9, FontWeight = FontWeight.SemiBold,
                    Foreground = AccentBrush(), Opacity = 0.9,
                });

            if (m.Attachment != null)
                stack.Children.Add(new TextBlock
                {
                    Text = "📎 " + m.Attachment.FileName, FontSize = 11,
                    Foreground = FontBrush(), TextWrapping = TextWrapping.Wrap,
                });

            if (!string.IsNullOrEmpty(m.Body) && m.Attachment == null)
                stack.Children.Add(new TextBlock
                {
                    Text = m.Body, FontSize = 11, Foreground = FontBrush(), TextWrapping = TextWrapping.Wrap,
                });

            stack.Children.Add(new TextBlock
            {
                Text = m.Timestamp.ToLocalTime().ToString("HH:mm"),
                FontSize = 8, Opacity = 0.5, Foreground = FontBrush(),
                HorizontalAlignment = HorizontalAlignment.Right,
            });

            var bubble = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 4),
                MaxWidth = 240,
                Margin = new Thickness(0, 1),
                HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = outgoing
                    ? new SolidColorBrush(Color.FromArgb(70, 70, 140, 230))
                    : new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            };

            // Drop the "Loading…/No messages" placeholder if present.
            if (MessageList.Children.Count == 1 && MessageList.Children[0] is TextBlock)
                MessageList.Children.Clear();
            MessageList.Children.Add(bubble);
        }

        private void ScrollToEnd() => Dispatcher.UIThread.Post(
            () => MessageScroller.Offset = MessageScroller.Offset.WithY(MessageScroller.Extent.Height),
            DispatcherPriority.Background);

        // ── Status ───────────────────────────────────────────────────────────────
        private void UpdateStatus()
        {
            var providers = CommsBridge.Providers;
            ConnStatus.Text = providers.Count == 0
                ? "No provider installed"
                : string.Join("\n", providers.Select(p => p.Status));
        }

        // ── Sizing + responsive collapse ───────────────────────────────────────────
        private void Adjust(int dw, int dh)
        {
            int newW = Math.Max(1, Math.Min(8, _currentW + dw));
            int newH = Math.Max(1, Math.Min(8, _currentH + dh));
            if (newW == _currentW && newH == _currentH) return;
            _currentW = newW; _currentH = newH;
            ApplySize();
            TileResizeRequested?.Invoke(_currentW, _currentH);
        }

        private void ApplySize()
        {
            if (RootBorder == null) return;
            RootBorder.Width = _currentW * CellW;
            RootBorder.Height = _currentH * CellH;
            EvaluateResponsive(_currentW * CellW);
        }

        private void EvaluateResponsive(double width)
        {
            if (width <= 0) return;
            var narrow = width < NarrowThreshold;
            if (narrow == _narrow && RootBorder != null) { ApplyResponsiveLayout(); return; }
            _narrow = narrow;
            if (!_narrow) _drawerOpen = false;
            ApplyResponsiveLayout();
        }

        private void ApplyResponsiveLayout()
        {
            if (_narrow)
            {
                ConvPane.Margin = new Thickness(0);
                MenuButton.IsVisible = true;
                SidebarBorder.IsVisible = _drawerOpen;
                Scrim.IsVisible = _drawerOpen;
            }
            else
            {
                ConvPane.Margin = new Thickness(SidebarWidth, 0, 0, 0);
                MenuButton.IsVisible = false;
                SidebarBorder.IsVisible = true;
                Scrim.IsVisible = false;
            }
        }

        private void SetDrawer(bool open)
        {
            if (!_narrow) return;
            _drawerOpen = open;
            ApplyResponsiveLayout();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private IBrush FontBrush()
            => this.TryFindResource("ColorModuleFont", out var r) && r is IBrush b ? b : Brushes.White;

        private IBrush AccentBrush()
            => this.TryFindResource("ColorAccent", out var r) && r is IBrush b ? b : new SolidColorBrush(Color.FromRgb(70, 140, 230));

        private TextBlock Muted(string s) => new()
        {
            Text = s, FontSize = 10, Opacity = 0.6, Foreground = FontBrush(),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4),
        };

        private static string GuessMime(string fileName)
        {
            var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                ".zip" => "application/zip",
                _ => "application/octet-stream",
            };
        }
    }
}
