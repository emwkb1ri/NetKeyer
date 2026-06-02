using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Flex.Smoothlake.FlexLib;

namespace NetKeyer.Views
{
    public partial class ConnectByIpDialog : Window
    {
        private enum Phase { IpEntry, ClientSelection }

        private sealed class GUIClientItem
        {
            public GUIClient Client { get; }
            public GUIClientItem(GUIClient client) { Client = client; }
            public override string ToString() => $"{Client.Station} [{Client.Program}]";
        }

        private Phase _phase = Phase.IpEntry;
        private readonly ObservableCollection<GUIClientItem> _clientItems = new();

        private readonly TaskCompletionSource<string> _connectTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GUIClient> _clientTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private StackPanel _ipInputPanel;
        private StackPanel _clientSelectionPanel;
        private TextBox _ipAddressTextBox;
        private TextBlock _searchingText;
        private ListBox _clientListBox;
        private TextBlock _statusText;
        private TextBlock _errorText;
        private ProgressBar _progressBar;
        private Button _actionButton;
        private Button _cancelButton;

        public ConnectByIpDialog()
        {
            InitializeComponent();
            this.Closing += OnClosing;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _ipInputPanel = this.FindControl<StackPanel>("IpInputPanel");
            _clientSelectionPanel = this.FindControl<StackPanel>("ClientSelectionPanel");
            _ipAddressTextBox = this.FindControl<TextBox>("IpAddressTextBox");
            _searchingText = this.FindControl<TextBlock>("SearchingText");
            _clientListBox = this.FindControl<ListBox>("ClientListBox");
            _statusText = this.FindControl<TextBlock>("StatusText");
            _errorText = this.FindControl<TextBlock>("ErrorText");
            _progressBar = this.FindControl<ProgressBar>("ProgressBar");
            _actionButton = this.FindControl<Button>("ActionButton");
            _cancelButton = this.FindControl<Button>("CancelButton");

            _clientListBox.ItemsSource = _clientItems;
            _clientListBox.SelectionChanged += ClientListBox_SelectionChanged;
        }

        // --- Public API called from ViewModel ---

        public void SetInitialIp(string ip)
        {
            if (_ipAddressTextBox != null)
                _ipAddressTextBox.Text = ip;
        }

        public Task<string> WaitForConnectAsync() => _connectTcs.Task;
        public Task<GUIClient> WaitForClientSelectionAsync() => _clientTcs.Task;

        public void UpdateStatus(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _statusText.Text = message;
            });
        }

        public void ShowError(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _errorText.Text = message;
                _errorText.IsVisible = true;
                _progressBar.IsVisible = false;
                _statusText.Text = "";
                _actionButton.IsEnabled = true;
                _actionButton.Content = "Connect";
                _phase = Phase.IpEntry;
                _ipInputPanel.IsVisible = true;
                _clientSelectionPanel.IsVisible = false;
            });
        }

        public void TransitionToPhase2()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _phase = Phase.ClientSelection;
                _ipInputPanel.IsVisible = false;
                _clientSelectionPanel.IsVisible = true;
                _progressBar.IsVisible = false;
                _statusText.Text = "";
                _actionButton.Content = "Select";
                _actionButton.IsEnabled = false;
            });
        }

        public void AddGuiClient(GUIClient client)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _clientItems.Add(new GUIClientItem(client));
                _searchingText.IsVisible = false;
            });
        }

        public void UpdateGuiClient(GUIClient client)
        {
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = 0; i < _clientItems.Count; i++)
                {
                    if (_clientItems[i].Client.ClientHandle == client.ClientHandle)
                    {
                        // Replace in-place to trigger ListBox refresh
                        _clientItems[i] = new GUIClientItem(client);
                        return;
                    }
                }
            });
        }

        public void RemoveGuiClient(GUIClient client)
        {
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = 0; i < _clientItems.Count; i++)
                {
                    if (_clientItems[i].Client.ClientHandle == client.ClientHandle)
                    {
                        bool wasSelected = _clientListBox.SelectedIndex == i;
                        _clientItems.RemoveAt(i);
                        if (wasSelected)
                            _actionButton.IsEnabled = false;
                        if (_clientItems.Count == 0)
                            _searchingText.IsVisible = true;
                        return;
                    }
                }
            });
        }

        // --- Event handlers ---

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_phase == Phase.IpEntry)
            {
                var ip = _ipAddressTextBox?.Text?.Trim() ?? "";
                if (!System.Net.IPAddress.TryParse(ip, out _))
                {
                    _errorText.Text = "Enter a valid IP address (e.g. 192.168.1.100)";
                    _errorText.IsVisible = true;
                    return;
                }
                _errorText.IsVisible = false;
                _actionButton.IsEnabled = false;
                _progressBar.IsVisible = true;
                _statusText.Text = "Connecting...";
                _connectTcs.TrySetResult(ip);
            }
            else
            {
                var selected = (_clientListBox.SelectedItem as GUIClientItem)?.Client;
                _clientTcs.TrySetResult(selected);
                Close();
            }
        }

        private void ClientListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_phase == Phase.ClientSelection)
                _actionButton.IsEnabled = _clientListBox.SelectedItem != null;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _connectTcs.TrySetResult(null);
            _clientTcs.TrySetResult(null);
            Close();
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            _connectTcs.TrySetResult(null);
            _clientTcs.TrySetResult(null);
        }
    }
}
