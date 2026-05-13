using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

        private const string PluginId = "discord-bridge-winui";
        private const string PluginName = "Discord Voice Bridge";
        private const string JakeyUrl = "ws://localhost:8889/";

        public MainWindow()
        {
            this.InitializeComponent();

            // --- THE FIX: Forcefully load native DLLs into memory ---
            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;

                // Load libdave, libsodium, and opus so Discord Voice works seamlessly
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libdave.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "libsodium.dll"));
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(basePath, "opus.dll"));

                Log("📦 Native audio libraries loaded successfully!");
            }
            catch (Exception ex)
            {
                Log($"⚠️ DLL Load Failed: {ex.Message}");
            }
            // --------------------------------------------------------

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("BotToken"))
            {
                TokenBox.Password = settings.Values["BotToken"].ToString();
                InviteBtn.IsEnabled = TokenBox.Password.Contains(".");
            }

            // Enable invite button when a token format is detected
            TokenBox.PasswordChanged += (s, e) => {
                InviteBtn.IsEnabled = TokenBox.Password.Contains(".");
            };
        }

        // --- Invite Button Logic ---
        private async void InviteBtn_Click(object sender, RoutedEventArgs e)
        {
            string token = TokenBox.Password;
            if (!token.Contains(".")) return;

            try
            {
                // 1. Extract Client ID from Token (The part before the first dot)
                string base64Id = token.Split('.')[0];

                // Pad the base64 string if necessary
                base64Id = base64Id.PadRight(base64Id.Length + (4 - base64Id.Length % 4) % 4, '=');
                byte[] data = Convert.FromBase64String(base64Id);
                string clientId = Encoding.UTF8.GetString(data);

                // 2. Define Permissions (Voice + Commands)
                // 2150535168 = View Channels, Send Messages, Connect, Speak, Use Commands
                long permissions = 2150535168;

                // 3. Build the URL
                string inviteUrl = $"https://discord.com/oauth2/authorize?client_id={clientId}&permissions={permissions}&scope=bot+applications.commands";

                // 4. Open in Browser
                await Windows.System.Launcher.LaunchUriAsync(new Uri(inviteUrl));
            }
            catch (Exception ex)
            {
                Log($"❌ Error generating invite: {ex.Message}");
            }
        }

        // --- Instruction Modal Logic ---
        private async void HowToTokenBtn_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };

            panel.Children.Add(new TextBlock { Text = "Step 1: Create Application", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            panel.Children.Add(new TextBlock { Text = "Go to the Discord Developer Portal and create a 'New Application'.", TextWrapping = TextWrapping.Wrap });

            panel.Children.Add(new TextBlock { Text = "Step 2: Get Token", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            panel.Children.Add(new TextBlock { Text = "Go to the 'Bot' tab, click 'Reset Token' to reveal it, and copy it into this app.", TextWrapping = TextWrapping.Wrap });

            panel.Children.Add(new TextBlock
            {
                Text = "Step 3: Enable Message Intent (Required)",
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.OrangeRed)
            });
            panel.Children.Add(new TextBlock { Text = "Scroll down on the 'Bot' page and enable 'Message Content Intent'.", TextWrapping = TextWrapping.Wrap });

            ContentDialog dialog = new ContentDialog
            {
                Title = "Discord Bot Setup Guide",
                Content = panel,
                PrimaryButtonText = "Open Dev Portal",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));
            }
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            string token = TokenBox.Password;
            if (string.IsNullOrWhiteSpace(token)) return;

            Windows.Storage.ApplicationData.Current.LocalSettings.Values["BotToken"] = token;

            Log("🚀 Starting services...");
            _cts = new CancellationTokenSource();

            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;

            await StartDiscord(token);
            _ = ConnectToJakeyTtsLoop();
        }

        private async Task StartDiscord(string token)
        {
            try
            {
                _discordClient = new DiscordSocketClient(new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates
                });

                _discordClient.Log += (m) => { Log($"[Discord] {m.Message}"); return Task.CompletedTask; };

                _discordClient.Connected += () => {
                    DispatcherQueue.TryEnqueue(() => DiscordStatusDot.Fill = new SolidColorBrush(Colors.Green));
                    return Task.CompletedTask;
                };

                // 1. Register Slash Commands when the bot is Ready
                _discordClient.Ready += async () => {
                    var joinCmd = new SlashCommandBuilder()
                        .WithName("join")
                        .WithDescription("Joins your current voice channel");

                    var leaveCmd = new SlashCommandBuilder()
                        .WithName("leave")
                        .WithDescription("Leaves the voice channel");

                    try
                    {
                        await _discordClient.CreateGlobalApplicationCommandAsync(joinCmd.Build());
                        await _discordClient.CreateGlobalApplicationCommandAsync(leaveCmd.Build());
                        Log("[Discord] Slash commands registered successfully.");
                    }
                    catch (Exception ex) { Log($"❌ Command Error: {ex.Message}"); }
                };

                // 2. Listen for Slash Command execution (FIXED: Non-blocking background thread)
                _discordClient.SlashCommandExecuted += (cmd) => {
                    _ = Task.Run(async () => {
                        try
                        {
                            if (cmd.Data.Name == "join") await JoinVoice(cmd);
                            if (cmd.Data.Name == "leave") await LeaveVoice(cmd);
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Command Execution Error: {ex.Message}");
                        }
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
            if (user?.VoiceChannel == null)
            {
                await cmd.RespondAsync("❌ You must be in a voice channel first!", ephemeral: true);
                return;
            }

            try
            {
                await cmd.RespondAsync($"🎙 Joining {user.VoiceChannel.Name}...");

                _currentAudioClient = await user.VoiceChannel.ConnectAsync();
                _discordAudioStream = _currentAudioClient.CreatePCMStream(AudioApplication.Mixed);

                Log($"🔊 Joined voice: {user.VoiceChannel.Name}");
            }
            catch (Exception ex)
            {
                Log($"❌ Voice Error: {ex.Message}");
                await cmd.FollowupAsync("Failed to join the voice channel.");
            }
        }

        private async Task LeaveVoice(SocketSlashCommand cmd)
        {
            var user = cmd.User as IGuildUser;
            if (user?.VoiceChannel != null)
            {
                await user.VoiceChannel.DisconnectAsync();
                await cmd.RespondAsync("👋 Disconnected.");
                Log("🔌 Disconnected from voice.");
            }
            else
            {
                await cmd.RespondAsync("I'm not in a voice channel!", ephemeral: true);
            }
            _discordAudioStream = null;
        }

        private async Task ConnectToJakeyTtsLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(JakeyUrl), _cts.Token);

                    DispatcherQueue.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Green));
                    Log("🔗 Connected to JakeyTTS Server.");

                    var reg = new
                    {
                        type = "register",
                        payload = new
                        {
                            id = PluginId,
                            name = PluginName,
                            version = "1.0",
                            protocol_version = "1.0",
                            subscriptions = new[] { "chat", "commands", "bits", "subs", "redeems", "test" }
                        }
                    };

                    await SendWsJson(reg);
                    await ReceiveLoop();
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Connection dropped. Retrying... ({ex.Message})");
                    DispatcherQueue.TryEnqueue(() => JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red));
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
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string type = root.GetProperty("type").GetString();

                if (type == "auth_status")
                {
                    bool approved = root.GetProperty("approved").GetBoolean();
                    Log(approved ? "✅ Access Authorized by User." : "⏳ Access Pending Approval in JakeyTTS UI.");
                }
                else if (type == "event_broadcast" && _discordAudioStream != null)
                {
                    string audioBase64 = root.GetProperty("payload").GetProperty("audio_base64").GetString();
                    await StreamToDiscord(Convert.FromBase64String(audioBase64));
                }
            }
        }

        private async Task StreamToDiscord(byte[] wavData)
        {
            // Skip the 44-byte WAV header
            if (wavData.Length <= 44) return;

            int pcmLen = wavData.Length - 44;

            // 24kHz Mono -> 48kHz Stereo means 1 input byte becomes 4 output bytes.
            byte[] upsampled = new byte[pcmLen * 4];
            int outIdx = 0;

            for (int i = 44; i < wavData.Length; i += 2)
            {
                // Read one 16-bit mono sample (2 bytes)
                byte b1 = wavData[i];
                byte b2 = wavData[i + 1];

                // Write to 48kHz Stereo (Duplicate across both channels AND time)

                // Frame 1 - Left Channel
                upsampled[outIdx++] = b1; upsampled[outIdx++] = b2;
                // Frame 1 - Right Channel
                upsampled[outIdx++] = b1; upsampled[outIdx++] = b2;

                // Frame 2 - Left Channel
                upsampled[outIdx++] = b1; upsampled[outIdx++] = b2;
                // Frame 2 - Right Channel
                upsampled[outIdx++] = b1; upsampled[outIdx++] = b2;
            }

            try
            {
                await _discordAudioStream.WriteAsync(upsampled, 0, upsampled.Length);
            }
            catch (Exception ex)
            {
                Log($"❌ Audio Stream Error: {ex.Message}");
            }
        }

        private void Log(string msg) => DispatcherQueue.TryEnqueue(() => {
            LogBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
            LogBlock.Select(LogBlock.Text.Length, 0);
        });

        private async Task SendWsJson(object obj)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }

        private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            if (_discordClient != null) await _discordClient.StopAsync();
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
            DiscordStatusDot.Fill = new SolidColorBrush(Colors.Red);
            JakeyStatusDot.Fill = new SolidColorBrush(Colors.Red);
        }
    }
}