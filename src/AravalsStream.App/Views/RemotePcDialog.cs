using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AravalsStream.App.Controls;
using AravalsStream.Core.RemoteCapture;

namespace AravalsStream.App.Views;

internal sealed class RemotePcDialog : Window
{
    internal sealed record AgentEntry(RemoteAgentAdvertisement Advertisement, string Host, DateTimeOffset LastSeen)
    {
        public override string ToString() => $"{Advertisement.ComputerName}  ·  {Advertisement.State}  ·  {Host}  ·  v{Advertisement.AgentVersion}  ·  {Advertisement.DisplayWidth}×{Advertisement.DisplayHeight}  ·  {string.Join(", ", Advertisement.Encoders)}";
    }

    private readonly PairedDeviceRegistry _registry;
    private readonly ListBox _agents = new();
    private readonly TextBox _host = new() { Width = 220, ToolTip = "Agent IP address or host name" };
    private readonly TextBox _code = new() { Width = 150, MaxLength = 6, ToolTip = "Temporary six-digit code shown in the Remote Agent" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue };
    private readonly Button _connect = new() { Content = "Connect", IsEnabled = false, MinWidth = 110 };
    private readonly RemoteDiscoveryService _discovery;
    private readonly DispatcherTimer _offlineTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public AgentEntry? SelectedAgent { get; private set; }

    public RemotePcDialog(Window owner, PairedDeviceRegistry registry, IReadOnlyList<AgentEntry> agents,
        RemoteDiscoveryService discovery)
    {
        Owner = owner; _registry = registry; _discovery = discovery;
        Title = "Add Remote PC"; Width = 720; Height = 500; MinWidth = 640; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = (Brush)owner.FindResource("BackgroundBrush");
        DarkWindowChrome.Apply(this);
        Build();
        _agents.SelectionChanged += (_, _) => UpdateSelection();
        _host.TextChanged += (_, _) => UpdateConnectEnabled();
        _discovery.AgentDiscovered += OnAgentDiscovered;
        _offlineTimer.Tick += (_, _) => MarkOfflineAgents();
        _offlineTimer.Start();
        Closed += (_, _) =>
        {
            _discovery.AgentDiscovered -= OnAgentDiscovered;
            _offlineTimer.Stop();
        };
        foreach (var agent in agents) _agents.Items.Add(agent);
        if (_agents.Items.Count > 0)
        {
            _agents.SelectedIndex = 0;
            UpdateSelection();
        }
    }

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "REMOTE PCS", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
        _agents.Margin = new Thickness(0, 40, 0, 12);
        Grid.SetRow(_agents, 1); root.Children.Add(_agents);
        var manual = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        manual.Children.Add(new TextBlock { Text = "Manual connection", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        manual.Children.Add(_host);
        var discover = new Button { Content = "Discover", Margin = new Thickness(8, 0, 0, 0) };
        discover.Click += async (_, _) => await DiscoverManualAsync();
        manual.Children.Add(discover);
        Grid.SetRow(manual, 2); root.Children.Add(manual);
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        codeRow.Children.Add(new TextBlock { Text = "Pairing code", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        codeRow.Children.Add(_code);
        codeRow.Children.Add(new TextBlock { Text = "Enter the temporary code currently shown in the Agent.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = Brushes.LightSteelBlue });
        Grid.SetRow(codeRow, 3); root.Children.Add(codeRow);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        _connect.Click += async (_, _) => await ConnectSelectedAsync();
        buttons.Children.Add(cancel); buttons.Children.Add(_connect);
        Grid.SetRow(footer, 4); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.Children.Add(footer);
        Content = root;
    }

    private async void UpdateSelection()
    {
        if (_agents.SelectedItem is not AgentEntry entry)
        {
            UpdateConnectEnabled();
            return;
        }
        _host.Text = entry.Host;
        UpdateConnectEnabled();
        if (string.Equals(entry.Advertisement.State, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Agent is offline.";
            return;
        }
        var devices = await _registry.ListAsync();
        if (_agents.SelectedItem is not AgentEntry current || current.Advertisement.DeviceId != entry.Advertisement.DeviceId) return;
        _status.Text = devices.Any(d => d.DeviceId == entry.Advertisement.DeviceId)
            ? "Paired device. Connect to use its protected SRT key."
            : "Pair with this Agent using the temporary code shown in its window.";
    }

    private void UpdateConnectEnabled() => _connect.IsEnabled = _agents.SelectedItem is AgentEntry entry &&
        !string.Equals(entry.Advertisement.State, "Offline", StringComparison.OrdinalIgnoreCase) && entry.Advertisement.Available;

    private void OnAgentDiscovered(RemoteAgentAdvertisement advertisement, System.Net.IPEndPoint endpoint)
    {
        if (!RemoteCaptureProtocol.IsCompatible(advertisement.ProtocolVersion)) return;
        var entry = new AgentEntry(advertisement, endpoint.Address.ToString(), DateTimeOffset.UtcNow);
        Dispatcher.BeginInvoke(() => UpsertAgent(entry));
    }

    private void UpsertAgent(AgentEntry entry)
    {
        var existingIndex = -1;
        for (var index = 0; index < _agents.Items.Count; index++)
        {
            if (_agents.Items[index] is AgentEntry existing && existing.Advertisement.DeviceId == entry.Advertisement.DeviceId)
            { existingIndex = index; break; }
        }
        if (existingIndex < 0)
        {
            _agents.Items.Add(entry);
            return;
        }

        var wasSelected = _agents.SelectedItem is AgentEntry selected && selected.Advertisement.DeviceId == entry.Advertisement.DeviceId;
        _agents.Items[existingIndex] = entry;
        if (wasSelected)
        {
            _agents.SelectedIndex = existingIndex;
            UpdateConnectEnabled();
            _status.Text = string.Equals(entry.Advertisement.State, "Offline", StringComparison.OrdinalIgnoreCase)
                ? "Agent is offline."
                : "Agent is available. Connect to pair or use the saved pairing.";
        }
    }

    private void MarkOfflineAgents()
    {
        for (var index = 0; index < _agents.Items.Count; index++)
        {
            if (_agents.Items[index] is not AgentEntry entry || DateTimeOffset.UtcNow - entry.LastSeen <= TimeSpan.FromSeconds(8) ||
                string.Equals(entry.Advertisement.State, "Offline", StringComparison.OrdinalIgnoreCase)) continue;
            var offline = entry with { Advertisement = entry.Advertisement with { State = "Offline", Available = false } };
            var wasSelected = _agents.SelectedIndex == index;
            _agents.Items[index] = offline;
            if (wasSelected)
            {
                _agents.SelectedIndex = index;
                _status.Text = "Agent is offline.";
            }
        }
    }

    private async Task DiscoverManualAsync()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) return;
        _status.Text = "Contacting Remote Agent…";
        try
        {
            var advertisement = await RemotePairingClient.DescribeAsync(host);
            if (!RemoteCaptureProtocol.IsCompatible(advertisement.ProtocolVersion))
                throw new InvalidOperationException("Remote Agent version is incompatible.");
            var entry = new AgentEntry(advertisement, host, DateTimeOffset.UtcNow);
            UpsertAgent(entry);
            _agents.SelectedItem = _agents.Items.Cast<AgentEntry>().FirstOrDefault(item => item.Advertisement.DeviceId == advertisement.DeviceId);
            _status.Text = "Agent found. Enter its temporary pairing code if this device is not paired yet.";
        }
        catch (Exception ex) { _status.Text = $"Connection failed: {ex.Message}"; }
    }

    private async Task ConnectSelectedAsync()
    {
        if (_agents.SelectedItem is not AgentEntry entry) return;
        var ad = entry.Advertisement;
        if (!RemoteCaptureProtocol.IsCompatible(ad.ProtocolVersion)) { _status.Text = "Remote Agent version is incompatible."; return; }
        try
        {
            var saved = (await _registry.ListAsync()).FirstOrDefault(d => d.DeviceId == ad.DeviceId);
            if (saved is null)
            {
                _status.Text = "Pairing securely…";
                var reply = await RemotePairingClient.PairAsync(entry.Host, ad.ControlPort, ad.DeviceId, _code.Text.Trim(), ad.CertificateThumbprint);
                if (string.IsNullOrWhiteSpace(reply.Passphrase)) throw new InvalidOperationException("Agent did not provide a transport key.");
                var device = new PairedRemoteDevice(ad.DeviceId, ad.ComputerName, entry.Host, ad.ControlPort,
                    ad.ProtocolVersion, ad.AgentVersion, reply.CertificateThumbprint ?? "", ad.DeviceId.ToString("N"), DateTimeOffset.UtcNow);
                await _registry.SaveAsync(device, reply.Passphrase);
            }
            else
            {
                var current = await RemotePairingClient.DescribeAsync(entry.Host, ad.ControlPort,
                    expectedCertificateThumbprint: saved.CertificateThumbprint);
                await _registry.RefreshMetadataAsync(saved.DeviceId, entry.Host, current);
                var savedPassphrase = await _registry.GetPassphraseAsync(ad.DeviceId);
                if (string.IsNullOrWhiteSpace(savedPassphrase)) throw new InvalidOperationException("Saved pairing secret is unavailable. Forget this device and pair it again.");
                // Metadata refresh is certificate-pinned and does not rewrite or rotate the stored secret.
            }
            SelectedAgent = entry;
            DialogResult = true;
        }
        catch (Exception ex) { _status.Text = $"Could not connect: {ex.Message}"; }
    }
}
