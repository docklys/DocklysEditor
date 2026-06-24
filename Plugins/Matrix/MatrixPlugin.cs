using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Docklys.ModuleContracts;

namespace DocklysPlugins.Matrix;

// Matrix communication-provider plugin. The user enters their homeserver +
// credentials once on the Plugins page; this plugin owns the session and
// publishes a single ICommsProvider on CommsBridge. The Comms module then talks
// to it through the contract without knowing anything about Matrix — and a
// future P2P plugin can register the same way alongside it.
//
// Like the Google Calendar plugin, the constructor publishes the provider the
// moment the host loads the plugin (registry load), so Comms sees the protocol —
// and any saved session reconnects — before the Plugins page is ever opened.
public sealed class MatrixPlugin : IPlugin
{
    public string PluginName    => "Matrix";
    public string PluginVersion => "1.0";
    public string UniquePluginId { get; private set; } = MatrixProvider.ProviderIdConst;
    public string PluginDescription =>
        "Connect Docklys Comms to a Matrix homeserver (default: your private server at matrix.qwqc.de). " +
        "Decentralised chat — point it at any homeserver.";

    public MatrixPlugin()
    {
        MatrixProvider.Instance.EnsureLoaded();           // load saved creds + auto-connect
        CommsBridge.Register(MatrixProvider.Instance);    // expose the protocol to Comms
    }

    public void SetPluginId(string uniquePluginId) => UniquePluginId = uniquePluginId;

    public Control CreateSettingsView(PluginContext ctx) => new MatrixView(ctx);
}

// Settings UI: homeserver / user / password + Connect, plus a live status line.
internal sealed class MatrixView : UserControl
{
    private readonly PluginContext _ctx;
    private readonly MatrixProvider _provider = MatrixProvider.Instance;
    private readonly TextBox _homeserver;
    private readonly TextBox _user;
    private readonly TextBox _password;
    private readonly TextBlock _status = new();
    private readonly Action _onChanged;

    public MatrixView(PluginContext ctx)
    {
        _ctx = ctx;
        var font   = new SolidColorBrush(ctx.Font);
        var accent = new SolidColorBrush(ctx.Accent);

        var help = new TextBlock
        {
            Text = "Enter your Matrix homeserver and login. Credentials stay on this machine; "
                 + "the Comms module reads conversations through the provider. "
                 + "Default homeserver is your private server at matrix.qwqc.de.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85, Foreground = font,
        };

        _homeserver = new TextBox
        {
            Text = ctx.GetSetting(MatrixProvider.KeyHomeserver) ?? MatrixProvider.DefaultHomeserver,
            Watermark = "https://matrix.qwqc.de",
            HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = font,
        };
        _user = new TextBox
        {
            Text = ctx.GetSetting(MatrixProvider.KeyUser) ?? "",
            Watermark = "username (e.g. lz)  or  @lz:matrix.qwqc.de",
            HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = font,
        };
        _password = new TextBox
        {
            Text = ctx.GetSetting(MatrixProvider.KeyPassword) ?? "",
            Watermark = "password", PasswordChar = '•',
            HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = font,
        };

        var connect = new Button { Content = "Save & Connect", Foreground = Brushes.White, Background = accent };
        connect.Click += async (_, _) => await SaveAndConnect();

        var disconnect = new Button { Content = "Disconnect", Foreground = font };
        disconnect.Click += async (_, _) => { await _provider.DisconnectAsync(); };

        _status.Foreground = font; _status.FontSize = 12; _status.Opacity = 0.9; _status.TextWrapping = TextWrapping.Wrap;

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                help,
                Label("Homeserver", font), _homeserver,
                Label("User", font), _user,
                Label("Password", font), _password,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connect, disconnect } },
                _status,
            },
        };

        _onChanged = () => Dispatcher.UIThread.Post(() => _status.Text = _provider.Status);
        _provider.Changed += _onChanged;
        DetachedFromVisualTree += (_, _) => _provider.Changed -= _onChanged;
        _status.Text = _provider.Status;
    }

    private async System.Threading.Tasks.Task SaveAndConnect()
    {
        _ctx.SetSetting(MatrixProvider.KeyHomeserver, NullIfEmpty(_homeserver.Text));
        _ctx.SetSetting(MatrixProvider.KeyUser,       NullIfEmpty(_user.Text));
        _ctx.SetSetting(MatrixProvider.KeyPassword,   NullIfEmpty(_password.Text));
        await _provider.DisconnectAsync();
        await _provider.ConnectAsync();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TextBlock Label(string s, IBrush font)
        => new() { Text = s, Foreground = font, FontSize = 12, Opacity = 0.85, Margin = new Thickness(0, 4, 0, 0) };
}
