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
using Microsoft.UI.Dispatching; // Required for DispatcherQueue
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

        // Explicitly capture the UI thread dispatcher
        private readonly DispatcherQueue _dispatcher;

        private const string PluginId = "discord-bridge-winui";
        private const string PluginName = "Discord Voice Bridge";
        private const string JakeyUrl = "ws://localhost:8889/";

        public MainWindow()
        {
            this.InitializeComponent();
            _dispatcher = this.DispatcherQueue; // Initialize dispatcher

            // Setup Modern Title Bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            this.AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

            // Load Native Audio Libraries
            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libdave.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libsodium.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "opus.dll"));
                Log("📦 Native audio libraries loaded.");
            }
            catch (Exception ex) { Log($"⚠️ DLL Load Failed: {ex.Message}"); }

            // Load Persistent Settings
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("BotToken"))
            {
                TokenBox.Password = settings.Values["BotToken"].ToString();
                InviteBtn.IsEnabled = TokenBox.Password.Contains(".");
            }

            TokenBox.PasswordChanged += (s, e) => { InviteBtn.IsEnabled = TokenBox.Password.Contains("."); };
            NavView.SelectedItem = NavView.MenuItems[0];
        }

        #region Navigation and UI Helpers

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            HomePage.Visibility = Visibility.Collapsed;
            EventsPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;

            if (args.IsSettingsInvoked) SettingsPage.Visibility = Visibility.Visible;
            else if (args.InvokedItemContainer?.Tag is string tag)
            {
                if (tag == "Home") HomePage.Visibility = Visibility.Visible;
                if (tag == "Events") EventsPage.Visibility = Visibility.Visible;
            }
        }

        private void Log(string msg) => _dispatcher.TryEnqueue(() => {
            LogBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
            LogBlock.Select(LogBlock.Text.Length, 0);
        });

        private async void OpenPortalBtn_Click(object sender, RoutedEventArgs e) =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));

        #endregion

        #region Event Logic & Real-time Sync

        private async void EventToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await SendRegistrationUpdate();
            }
        }

        private async Task SendRegistrationUpdate()
        {
            try
            {
                List<string> activeSubs = new List<string>();

                // Ensure UI elements are read on the UI thread
                await _dispatcher.EnqueueAsync(() => {
                    if (ToggleTest.IsOn) activeSubs.Add("test");
                    if (ToggleCommands.IsOn) activeSubs.Add("commands");
                    if (ToggleRedeems.IsOn) activeSubs.Add("redeems");
                    if (ToggleBits.IsOn) activeSubs.Add("bits");
                    if (ToggleSubs.IsOn) activeSubs.Add("subs");
                    if (ToggleChat.IsOn) activeSubs.Add("chat");
                    if (activeSubs.Count == 0) activeSubs.Add("none");
                });

                var reg = new PluginRegisterMsg
                {
                    type = "register",
                    payload = new PluginRegisterPayload
                    {
                        id = PluginId,
                        name = PluginName,
                        version = "1.0",
                        protocol_version = "1.0",
                        subscriptions = activeSubs.ToArray()
                    }
                };

                string jsonString = JsonSerializer.Serialize(reg, PluginJsonContext.Default.PluginRegisterMsg);
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonString)),
                        WebSocketMessageType.Text, true, _cts.Token);
                    Log("📡 Broadcast preferences synchronized.");
                }
            }
            catch (Exception ex) { Log($"❌ Sync Error: {ex.Message}"); }
        }

        #endregion

        #region Discord Logic

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            string token = TokenBox.Password;
            if (string.IsNullOrWhiteSpace(token))
            {
                Log("⚠️ No Token provided!");
                return;
            }

            try
            {
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["BotToken"] = token;
                _cts = new CancellationTokenSource();

                // Update UI State
                ConnectBtn.IsEnabled = false;
                DisconnectBtn.IsEnabled = true;

                Log("🚀 Initializing...");

                // Start Discord and WebSocket loop without blocking the UI
                _ = StartDiscord(token);
                _ = ConnectToJakeyTtsLoop();
            }
            catch (Exception ex)
            {
                Log($"❌ Startup Crash: {ex.Message}");
                ConnectBtn.IsEnabled = true;
                DisconnectBtn.IsEnabled = false;
            }
        }

        private async Task StartDiscord(string token)
        {
            try
            {
                _discordClient = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates });
                _discordClient.Log += (m) => { Log($"[Discord] {m.Message}"); return Task.CompletedTask; };
                _discordClient.Connected += () => { _dispatcher.TryEnqueue(() => DiscordStatusDot.Fill = new SolidColorBrush(Colors.Green)); return Task.CompletedTask; };

                _discordClient.Ready += async () => {
                    await _discordClient.CreateGlobalApplicationCommandAsync(new SlashCommandBuilder().WithName("join").WithDescription("Join current voice").Build());
                    await _discordClient.CreateGlobalApplicationCommandAsync(new SlashCommandBuilder().WithName("leave").WithDescription("Leave voice").Build());
                };

                _discordClient.SlashCommandExecuted += (cmd) => {
                    _ = Task.Run(async () => {
                        if (cmd.Data.Name == "join") await JoinVoice(cmd);
                        if (cmd.Data.Name == "leave") await LeaveVoice(cmd);
                    });
                    return Task.CompletedTask;
                };

                await _discordClient.LoginAsync(TokenType.Bot, token);
                await _discordClient.StartAsync();
            }
            catch (Exception ex) { Log($"❌ Discord Error: {ex.Message}"); }
        }

        private async Task JoinVoice(SocketSlashCommand cmd)
        {
            var user = cmd.User as IGuildUser;
            if (user?.VoiceChannel == null) { await cmd.RespondAsync("Join a voice channel first!", ephemeral: true); return; }
            await cmd.RespondAsync($"🎙 Joining {user.VoiceChannel.Name}...");
            _currentAudioClient = await user.VoiceChannel.ConnectAsync();
            _discordAudioStream = _currentAudioClient.CreatePCMStream(AudioApplication.Mixed);
        }

        private async Task LeaveVoice(SocketSlashCommand cmd)
        {
            var user = cmd.User as IGuildUser;
            if (user?.VoiceChannel != null) await user.VoiceChannel.DisconnectAsync();
            await cmd.RespondAsync("👋 Disconnected.");
            _discordAudioStream = null;
        }

        #endregion

        #region JakeyTTS WebSocket Logic

        private async Task ConnectToJakeyTtsLoop()
        {
            while (_cts != null && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(JakeyUrl), _cts.Token);
                    _dispatcher.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Green));
                    Log("🔗 Linked to JakeyTTS Server.");

                    await SendRegistrationUpdate();
                    await ReceiveLoop();
                }
                catch (Exception ex)
                {
                    _dispatcher.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red));
                    Log($"⚠️ WebSocket Offline: {ex.Message}. Retrying in 5s...");
                    await Task.Delay(5000);
                }
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[1024 * 512];
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() == "event_broadcast" && _discordAudioStream != null)
                {
                    string audioBase64 = root.GetProperty("payload").GetProperty("audio_base64").GetString();
                    await StreamToDiscord(Convert.FromBase64String(audioBase64));
                }
                else if (root.GetProperty("type").GetString() == "auth_status")
                {
                    Log(root.GetProperty("approved").GetBoolean() ? "✅ Access Authorized." : "⏳ Access Pending in JakeyTTS.");
                }
            }
        }

        private async Task StreamToDiscord(byte[] wavData)
        {
            // A standard WAV header is 44 bytes. We only want the raw PCM data after it.
            if (wavData.Length <= 44 || _discordAudioStream == null) return;

            int pcmLen = wavData.Length - 44;

            // We are converting 24kHz Mono -> 48kHz Stereo.
            // 1. To get 24kHz to 48kHz, we double every sample (x2).
            // 2. To get Mono to Stereo, we double every channel (x2).
            // Total size increase: x4.
            byte[] upsampled = new byte[pcmLen * 4];
            int outIdx = 0;

            for (int i = 44; i < wavData.Length; i += 2)
            {
                // Get the 16-bit sample (2 bytes)
                byte b1 = wavData[i];
                byte b2 = wavData[i + 1];

                // Discord expects: [LeftLow, LeftHigh, RightLow, RightHigh]
                // We repeat this exact 4-byte block twice to turn 24kHz into 48kHz.

                // Frame 1
                upsampled[outIdx++] = b1; // Left
                upsampled[outIdx++] = b2;
                upsampled[outIdx++] = b1; // Right
                upsampled[outIdx++] = b2;

                // Frame 2 (This duplication is what corrects the pitch/speed)
                upsampled[outIdx++] = b1; // Left
                upsampled[outIdx++] = b2;
                upsampled[outIdx++] = b1; // Right
                upsampled[outIdx++] = b2;
            }

            try
            {
                await _discordAudioStream.WriteAsync(upsampled, 0, upsampled.Length);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Audio Stream Error: {ex.Message}");
            }
        }

        #endregion

        private async void InviteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TokenBox.Password.Contains(".")) return;
            string base64Id = TokenBox.Password.Split('.')[0];
            base64Id = base64Id.PadRight(base64Id.Length + (4 - base64Id.Length % 4) % 4, '=');
            string clientId = Encoding.UTF8.GetString(Convert.FromBase64String(base64Id));
            string url = $"https://discord.com/oauth2/authorize?client_id={clientId}&permissions=2150535168&scope=bot+applications.commands";
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }

        private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            if (_discordClient != null) await _discordClient.StopAsync();
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
            _dispatcher.TryEnqueue(() => {
                DiscordStatusDot.Fill = new SolidColorBrush(Colors.Red);
                JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red);
            });
            Log("🛑 Services stopped.");
        }
    }

    #region Helpers & Context

    public class PluginRegisterPayload
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string version { get; set; } = "";
        public string protocol_version { get; set; } = "";
        public string[] subscriptions { get; set; } = Array.Empty<string>();
    }

    public class PluginRegisterMsg
    {
        public string type { get; set; } = "register";
        public PluginRegisterPayload payload { get; set; } = new();
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
    [JsonSerializable(typeof(PluginRegisterMsg))]
    internal partial class PluginJsonContext : JsonSerializerContext { }

    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this DispatcherQueue dq, Action action)
        {
            var tcs = new TaskCompletionSource();
            dq.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }
    }

    #endregion
}