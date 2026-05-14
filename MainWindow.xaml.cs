using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Discord;
using Discord.Audio;
using Discord.WebSocket;

namespace JakeyTTS.DiscordBridge
{
    public sealed partial class MainWindow : Window
    {
        private DiscordSocketClient _discordClient;
        private ClientWebSocket _webSocket;
        private IAudioClient _currentAudioClient;
        private AudioOutStream _discordAudioStream;
        private CancellationTokenSource _cts;
        private readonly DispatcherQueue _dispatcher;

        private int _retryCount = 0;
        private const int MaxRetries = 5;
        private string _appIconBase64 = "";

        private readonly Dictionary<string, SocketSlashCommand> _pendingRequests = new();

        private const string PluginId = "discord-bridge-winui";
        private const string PluginName = "Discord Voice Bridge";

        public MainWindow()
        {
            this.InitializeComponent();
            _dispatcher = this.DispatcherQueue;
            LoadAppIcon();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            TokenBox.Password = settings.Values["BotToken"]?.ToString() ?? "";
            ServerUrlBox.Text = settings.Values["WsUrl"]?.ToString() ?? "ws://localhost:8889/";

            int savedTheme = (int)(settings.Values["AppTheme"] ?? 0);
            ThemeBox.SelectedIndex = savedTheme;
            ApplyTheme(savedTheme);

            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libsodium.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "opus.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libdave.dll"));
            }
            catch { }

            TokenBox.PasswordChanged += (s, e) => { InviteBtn.IsEnabled = TokenBox.Password.Contains("."); };
            NavView.SelectedItem = NavView.MenuItems[0];
        }

        private void LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets/Square44x44Logo.scale-200.png");
                if (File.Exists(path)) _appIconBase64 = Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch { }
        }

        #region UI Logic & Instructions
        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            HomePage.Visibility = Visibility.Collapsed;
            EventsPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            CommandsPage.Visibility = Visibility.Collapsed;

            if (args.IsSettingsInvoked) SettingsPage.Visibility = Visibility.Visible;
            else if (args.InvokedItemContainer?.Tag is string tag)
            {
                if (tag == "Home") HomePage.Visibility = Visibility.Visible;
                if (tag == "Events") EventsPage.Visibility = Visibility.Visible;
                if (tag == "Commands") CommandsPage.Visibility = Visibility.Visible;
            }
        }

        private async void ShowTokenInstructions_Click(object sender, RoutedEventArgs e)
        {
            var stack = new StackPanel { Spacing = 12, Padding = new Thickness(0, 10, 0, 0) };
            stack.Children.Add(new TextBlock { Text = "1. Create App", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            stack.Children.Add(new TextBlock { Text = "Go to Discord Developer Portal and create a 'New Application'.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "2. Get Token", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            stack.Children.Add(new TextBlock { Text = "Go to the 'Bot' tab, Reset/Copy the Token.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "3. Enable Intent", FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Colors.OrangeRed) });
            stack.Children.Add(new TextBlock { Text = "Scroll down in 'Bot' tab and enable 'Message Content Intent'.", TextWrapping = TextWrapping.Wrap });

            ContentDialog dialog = new ContentDialog
            {
                Title = "Discord Bot Setup",
                Content = stack,
                PrimaryButtonText = "Developer Portal",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));
        }

        private void ApplyTheme(int index)
        {
            if (this.Content is FrameworkElement fe) fe.RequestedTheme = index switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["AppTheme"] = ThemeBox.SelectedIndex;
            ApplyTheme(ThemeBox.SelectedIndex);
        }

        private void Log(string msg) => _dispatcher.TryEnqueue(() => {
            LogBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
            LogBlock.Select(LogBlock.Text.Length, 0);
        });

        private async void OpenPortalBtn_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));

        private async void InviteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TokenBox.Password.Contains(".")) return;
            string base64Id = TokenBox.Password.Split('.')[0].PadRight(TokenBox.Password.Split('.')[0].Length + (4 - TokenBox.Password.Split('.')[0].Length % 4) % 4, '=');
            string clientId = Encoding.UTF8.GetString(Convert.FromBase64String(base64Id));
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://discord.com/oauth2/authorize?client_id={clientId}&permissions=2150535168&scope=bot+applications.commands"));
        }

        private async void EventToggle_Toggled(object sender, RoutedEventArgs e) { if (_webSocket?.State == WebSocketState.Open) await SendRegistrationUpdate(); }
        #endregion

        #region WebSocket & Receive Loop
        private async Task ConnectToJakeyTtsLoop()
        {
            string url = ServerUrlBox.Text;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["WsUrl"] = url;
            while (_cts != null && !_cts.Token.IsCancellationRequested)
            {
                if (_retryCount >= MaxRetries) { Log("🛑 Failed after 5 attempts."); break; }
                try
                {
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(url), _cts.Token);
                    _retryCount = 0;
                    _dispatcher.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Green));
                    await SendRegistrationUpdate();
                    await ReceiveLoop();
                }
                catch
                {
                    _retryCount++;
                    _dispatcher.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red));
                    await Task.Delay(5000);
                }
            }
        }

        private async Task ReceiveLoop()
        {
            var chunkBuffer = new byte[1024 * 16];
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                try
                {
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(chunkBuffer), _cts.Token);
                        ms.Write(chunkBuffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString()!;

                    if (type == "event_broadcast" && _discordAudioStream != null)
                    {
                        await StreamToDiscord(Convert.FromBase64String(root.GetProperty("payload").GetProperty("audio_base64").GetString()!));
                    }
                    else if (type == "tts_response")
                    {
                        string reqId = root.GetProperty("request_id").GetString()!;
                        if (_pendingRequests.TryGetValue(reqId, out var cmd))
                        {
                            byte[] audioBytes = Convert.FromBase64String(root.GetProperty("payload").GetProperty("audio_base64").GetString()!);
                            if (audioBytes.Length > 44)
                            {
                                if (cmd.Data.Name == "file")
                                {
                                    using var uploadStream = new MemoryStream(audioBytes);
                                    uploadStream.Position = 0; // Fix silent file
                                    await cmd.FollowupWithFileAsync(uploadStream, "jakey_tts.wav", "🔊 Your audio is ready!");
                                }
                                else if (cmd.Data.Name == "speak")
                                {
                                    await StreamToDiscord(audioBytes);
                                    await cmd.FollowupAsync("🎙 Audio streamed.");
                                }
                            }
                            _pendingRequests.Remove(reqId);
                        }
                    }
                }
                catch (Exception ex) { Log($"❌ WebSocket Error: {ex.Message}"); }
            }
        }

        private async Task SendRegistrationUpdate()
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            List<string> subs = new List<string>();
            _dispatcher.TryEnqueue(() => {
                if (ToggleTest.IsOn) subs.Add("test"); if (ToggleCommands.IsOn) subs.Add("commands"); if (ToggleRedeems.IsOn) subs.Add("redeems");
                if (ToggleBits.IsOn) subs.Add("bits"); if (ToggleSubs.IsOn) subs.Add("subs"); if (ToggleChat.IsOn) subs.Add("chat");
            });
            var reg = new PluginRegisterMsg { type = "register", payload = new PluginRegisterPayload { id = PluginId, name = PluginName, icon_base64 = _appIconBase64, subscriptions = subs.ToArray() } };
            await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reg, PluginJsonContext.Default.PluginRegisterMsg))), WebSocketMessageType.Text, true, _cts.Token);
        }
        #endregion

        #region Discord Logic
        private async void UpdateCommands_Click(object sender, RoutedEventArgs e) { if (_discordClient?.ConnectionState == ConnectionState.Connected) await RegisterSlashCommands(); }

        private async Task RegisterSlashCommands()
        {
            var commands = new List<SlashCommandProperties>();
            if (ToggleJoinCmd.IsOn) commands.Add(new SlashCommandBuilder().WithName("join").WithDescription("Join voice").Build());
            if (ToggleLeaveCmd.IsOn) commands.Add(new SlashCommandBuilder().WithName("leave").WithDescription("Leave voice").Build());
            if (ToggleSpeakCmd.IsOn) commands.Add(new SlashCommandBuilder().WithName("speak").WithDescription("TTS in call").AddOption("text", ApplicationCommandOptionType.String, "What to say", isRequired: true).Build());
            if (ToggleFileCmd.IsOn) commands.Add(new SlashCommandBuilder().WithName("file").WithDescription("TTS to file").AddOption("text", ApplicationCommandOptionType.String, "Text for file", isRequired: true).Build());
            try { await _discordClient.BulkOverwriteGlobalApplicationCommandsAsync(commands.ToArray()); Log("✅ Commands synced."); } catch { }
        }

        private async Task StartDiscord(string token)
        {
            try
            {
                _discordClient = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates });
                _discordClient.Connected += () => { _dispatcher.TryEnqueue(() => DiscordStatusDot.Fill = new SolidColorBrush(Colors.Green)); return Task.CompletedTask; };
                _discordClient.Ready += async () => { await RegisterSlashCommands(); };
                _discordClient.SlashCommandExecuted += async (cmd) => {
                    await cmd.DeferAsync();
                    _ = Task.Run(async () => {
                        try
                        {
                            string reqId = Guid.NewGuid().ToString();
                            if (cmd.Data.Name == "join")
                            {
                                var user = cmd.User as IGuildUser;
                                if (user?.VoiceChannel == null) { await cmd.FollowupAsync("Join a channel first!"); return; }
                                _currentAudioClient = await user.VoiceChannel.ConnectAsync();
                                _discordAudioStream = _currentAudioClient.CreatePCMStream(AudioApplication.Mixed);
                                await cmd.FollowupAsync($"🎙 Joined {user.VoiceChannel.Name}");
                            }
                            else if (cmd.Data.Name == "leave")
                            {
                                var user = cmd.User as IGuildUser; if (user?.VoiceChannel != null) await user.VoiceChannel.DisconnectAsync();
                                _discordAudioStream = null; await cmd.FollowupAsync("👋 Left.");
                            }
                            else if (cmd.Data.Name == "speak" || cmd.Data.Name == "file")
                            {
                                if (cmd.Data.Name == "speak" && _discordAudioStream == null) { await cmd.FollowupAsync("❌ Join voice first."); return; }
                                _pendingRequests[reqId] = cmd;
                                await RequestJakeyTts(cmd.Data.Options.First().Value.ToString()!, reqId);
                            }
                        }
                        catch { }
                    });
                };
                await _discordClient.LoginAsync(TokenType.Bot, token);
                await _discordClient.StartAsync();
            }
            catch { }
        }

        private async Task RequestJakeyTts(string text, string reqId)
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            var req = new { type = "tts_request", request_id = reqId, payload = new { text = text, voice = "default", speed = 1.0f } };
            await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req))), WebSocketMessageType.Text, true, _cts.Token);
        }

        private async Task StreamToDiscord(byte[] wavData)
        {
            if (wavData.Length <= 44 || _discordAudioStream == null) return;
            int pcmLen = wavData.Length - 44;
            byte[] upsampled = new byte[pcmLen * 4];
            int outIdx = 0;
            for (int i = 44; i < wavData.Length; i += 2)
            {
                byte b1 = wavData[i]; byte b2 = wavData[i + 1];
                for (int j = 0; j < 2; j++) { upsampled[outIdx++] = b1; upsampled[outIdx++] = b2; upsampled[outIdx++] = b1; upsampled[outIdx++] = b2; }
            }
            try { await _discordAudioStream.WriteAsync(upsampled, 0, upsampled.Length); } catch { }
        }
        #endregion

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TokenBox.Password)) return;
            _retryCount = 0; _cts = new CancellationTokenSource();
            ConnectBtn.IsEnabled = false; DisconnectBtn.IsEnabled = true;
            _ = StartDiscord(TokenBox.Password); _ = ConnectToJakeyTtsLoop();
        }

        private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel(); if (_discordClient != null) await _discordClient.StopAsync();
            _dispatcher.TryEnqueue(() => { ConnectBtn.IsEnabled = true; DisconnectBtn.IsEnabled = false; DiscordStatusDot.Fill = new SolidColorBrush(Colors.Red); JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red); });
        }
    }

    #region Models
    public class PluginRegisterPayload { public string id { get; set; } = ""; public string name { get; set; } = ""; public string icon_base64 { get; set; } = ""; public string version { get; set; } = "1.0.0"; public string protocol_version { get; set; } = "1.0"; public string[] subscriptions { get; set; } = Array.Empty<string>(); }
    public class PluginRegisterMsg { public string type { get; set; } = "register"; public PluginRegisterPayload payload { get; set; } = new(); }
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
    [JsonSerializable(typeof(PluginRegisterMsg))]
    internal partial class PluginJsonContext : JsonSerializerContext { }
    public static class DispatcherQueueExtensions { public static Task EnqueueAsync(this DispatcherQueue dq, Action action) { var tcs = new TaskCompletionSource(); dq.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }); return tcs.Task; } }
    #endregion
}