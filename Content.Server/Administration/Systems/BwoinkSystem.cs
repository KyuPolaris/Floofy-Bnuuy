using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Server.Players.RateLimiting;
using Content.Server.Preferences.Managers;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Players.RateLimiting;
using JetBrains.Annotations;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Systems
{
    [UsedImplicitly]
    public sealed partial class BwoinkSystem : SharedBwoinkSystem
    {
        private const string RateLimitKey = "AdminHelp";

        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IAdminManager _adminManager = default!;
        [Dependency] private readonly IConfigurationManager _config = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPlayerLocator _playerLocator = default!;
        [Dependency] private readonly GameTicker _gameTicker = default!;
        [Dependency] private readonly SharedMindSystem _minds = default!;
        [Dependency] private readonly IAfkManager _afkManager = default!;
        [Dependency] private readonly IServerDbManager _dbManager = default!;
        [Dependency] private readonly PlayerRateLimitManager _rateLimit = default!;
        [Dependency] private readonly IServerPreferencesManager _preferencesManager = default!;

        [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord\.com/api/webhooks/(\d+)/((?!.*/).*)$")]
        private static partial Regex DiscordRegex();

        private string _webhookUrl = string.Empty;
        private WebhookData? _webhookData;

        private string _onCallUrl = string.Empty;
        private WebhookData? _onCallData;

        private ISawmill _sawmill = default!;
        private readonly HttpClient _httpClient = new();

        private string _footerIconUrl = string.Empty;
        private string _avatarUrl = string.Empty;
        private string _serverName = string.Empty;

        private readonly Dictionary<NetUserId, DiscordRelayInteraction> _relayMessages = new();
        private Dictionary<NetUserId, string> _oldMessageIds = new();
        private readonly Dictionary<NetUserId, Queue<DiscordRelayedData>> _messageQueues = new();
        private readonly HashSet<NetUserId> _processingChannels = new();
        private readonly Dictionary<NetUserId, (TimeSpan Timestamp, bool Typing)> _typingUpdateTimestamps = new();
        private string _overrideClientName = string.Empty;

        // Max embed description length is 4096, according to https://discord.com/developers/docs/resources/channel#embed-object-embed-limits
        // Keep small margin, just to be safe
        private const ushort DescriptionMax = 4000;

        // Maximum length a message can be before it is cut off
        // Should be shorter than DescriptionMax
        private const ushort MessageLengthCap = 3000;

        // Text to be used to cut off messages that are too long. Should be shorter than MessageLengthCap
        private const string TooLongText = "... **(too long)**";

        private int _maxAdditionalChars;
        private readonly Dictionary<NetUserId, DateTime> _activeConversations = new();

        public override void Initialize()
        {
            base.Initialize();

            Subs.CVar(_config, CCVars.DiscordOnCallWebhook, OnCallChanged, true);

            Subs.CVar(_config, CCVars.DiscordAHelpWebhook, OnWebhookChanged, true);
            Subs.CVar(_config, CCVars.DiscordAHelpFooterIcon, OnFooterIconChanged, true);
            Subs.CVar(_config, CCVars.DiscordAHelpAvatar, OnAvatarChanged, true);
            Subs.CVar(_config, CVars.GameHostName, OnServerNameChanged, true);
            Subs.CVar(_config, CCVars.AdminAhelpOverrideClientName, OnOverrideChanged, true);
            _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("AHELP");
            var defaultParams = new AHelpMessageParams(
                string.Empty,
                string.Empty,
                true,
                _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                _gameTicker.RunLevel,
                playedSound: false
            );
            _maxAdditionalChars = GenerateAHelpMessage(defaultParams).Message.Length;
            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

            SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
            SubscribeNetworkEvent<BwoinkClientTypingUpdated>(OnClientTypingUpdated);
            SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _activeConversations.Clear());

        	_rateLimit.Register(
                RateLimitKey,
                new RateLimitRegistration(
                    CCVars.AhelpRateLimitPeriod,
                    CCVars.AhelpRateLimitCount,
                    PlayerRateLimitedAction)
                );
        }

        private async void OnCallChanged(string url)
        {
            _onCallUrl = url;

            if (url == string.Empty)
                return;

            var match = DiscordRegex().Match(url);

            if (!match.Success)
            {
                Log.Error("On call URL does not appear to be valid.");
                return;
            }

            if (match.Groups.Count <= 2)
            {
                Log.Error("Could not get webhook ID or token for on call URL.");
                return;
            }

            var webhookId = match.Groups[1].Value;
            var webhookToken = match.Groups[2].Value;

            _onCallData = await GetWebhookData(url);
        }

        private void PlayerRateLimitedAction(ICommonSession obj)
        {
            RaiseNetworkEvent(
                new BwoinkTextMessage(obj.UserId, default, Loc.GetString("bwoink-system-rate-limited"), playSound: false),
                obj.Channel);
        }

        private void OnOverrideChanged(string obj)
        {
            _overrideClientName = obj;
        }

        private async void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
        {
            if (e.NewStatus == SessionStatus.Disconnected)
            {
                if (_activeConversations.TryGetValue(e.Session.UserId, out var lastMessageTime))
                {
                    var timeSinceLastMessage = DateTime.Now - lastMessageTime;
                    if (timeSinceLastMessage > TimeSpan.FromMinutes(5))
                    {
                        _activeConversations.Remove(e.Session.UserId);
                        return; // Do not send disconnect message if timeout exceeded
                    }
                }

                // Check if the user has been banned
                var ban = await _dbManager.GetServerBanAsync(null, e.Session.UserId, null, null);
                if (ban != null)
                {
                    var banMessage = Loc.GetString("bwoink-system-player-banned", ("banReason", ban.Reason));
                    NotifyAdmins(e.Session, banMessage, PlayerStatusType.Banned);
                    _activeConversations.Remove(e.Session.UserId);
                    return;
                }
            }

            // Notify all admins if a player disconnects or reconnects
            var message = e.NewStatus switch
            {
                SessionStatus.Connected => Loc.GetString("bwoink-system-player-reconnecting"),
                SessionStatus.Disconnected => Loc.GetString("bwoink-system-player-disconnecting"),
                _ => null
            };

            if (message != null)
            {
                var statusType = e.NewStatus == SessionStatus.Connected
                    ? PlayerStatusType.Connected
                    : PlayerStatusType.Disconnected;
                NotifyAdmins(e.Session, message, statusType);
            }

            if (e.NewStatus != SessionStatus.InGame)
                return;

            RaiseNetworkEvent(new BwoinkDiscordRelayUpdated(!string.IsNullOrWhiteSpace(_webhookUrl)), e.Session);
        }

        private void NotifyAdmins(ICommonSession session, string message, PlayerStatusType statusType)
        {
            if (!_activeConversations.ContainsKey(session.UserId))
            {
                // If the user is not part of an active conversation, do not notify admins.
                return;
            }

            // Get the current timestamp
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var roundTime = _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss");

            // Determine the icon based on the status type
            string icon = statusType switch
            {
                PlayerStatusType.Connected => ":green_circle:",
                PlayerStatusType.Disconnected => ":red_circle:",
                PlayerStatusType.Banned => ":no_entry:",
                _ => ":question:"
            };

            // Create the message parameters for Discord
            var messageParams = new AHelpMessageParams(
                session.Name,
                message,
                true,
                roundTime,
                _gameTicker.RunLevel,
                playedSound: true,
                icon: icon
            );

            // Create the message for in-game with username
            var color = statusType switch
            {
                PlayerStatusType.Connected => Color.Green.ToHex(),
                PlayerStatusType.Disconnected => Color.Yellow.ToHex(),
                PlayerStatusType.Banned => Color.Orange.ToHex(),
                _ => Color.Gray.ToHex(),
            };
            var inGameMessage = $"[color={color}]{session.Name} {message}[/color]";

            var bwoinkMessage = new BwoinkTextMessage(
                userId: session.UserId,
                trueSender: SystemUserId,
                text: inGameMessage,
                sentAt: DateTime.Now,
                playSound: false
            );

            var admins = GetTargetAdmins();
            foreach (var admin in admins)
            {
                RaiseNetworkEvent(bwoinkMessage, admin);
            }

            // Enqueue the message for Discord relay
            if (_webhookUrl != string.Empty)
            {
                // if (!_messageQueues.ContainsKey(session.UserId))
                //     _messageQueues[session.UserId] = new Queue<string>();
                //
                // var escapedText = FormattedMessage.EscapeText(message);
                // messageParams.Message = escapedText;
                //
                // var discordMessage = GenerateAHelpMessage(messageParams);
                // _messageQueues[session.UserId].Enqueue(discordMessage);

                var queue = _messageQueues.GetOrNew(session.UserId);
                var escapedText = FormattedMessage.EscapeText(message);
                messageParams.Message = escapedText;
                var discordMessage = GenerateAHelpMessage(messageParams);
                queue.Enqueue(discordMessage);
            }
        }

        private void OnGameRunLevelChanged(GameRunLevelChangedEvent args)
        {
            // Don't make a new embed if we
            // 1. were in the lobby just now, and
            // 2. are not entering the lobby or directly into a new round.
            if (args.Old is GameRunLevel.PreRoundLobby ||
                args.New is not (GameRunLevel.PreRoundLobby or GameRunLevel.InRound))
            {
                return;
            }

            // Store the Discord message IDs of the previous round
            _oldMessageIds = new Dictionary<NetUserId, string>();
            foreach (var (user, interaction) in _relayMessages)
            {
                var id = interaction.Id;
                if (id == null)
                    return;

                _oldMessageIds[user] = id;
            }

            _relayMessages.Clear();
        }

        private void OnClientTypingUpdated(BwoinkClientTypingUpdated msg, EntitySessionEventArgs args)
        {
            if (_typingUpdateTimestamps.TryGetValue(args.SenderSession.UserId, out var tuple) &&
                tuple.Typing == msg.Typing &&
                tuple.Timestamp + TimeSpan.FromSeconds(1) > _timing.RealTime)
            {
                return;
            }

            _typingUpdateTimestamps[args.SenderSession.UserId] = (_timing.RealTime, msg.Typing);

            // Non-admins can only ever type on their own ahelp, guard against fake messages
            var isAdmin = _adminManager.GetAdminData(args.SenderSession)?.HasFlag(AdminFlags.Adminhelp) ?? false;
            var channel = isAdmin ? msg.Channel : args.SenderSession.UserId;
            var update = new BwoinkPlayerTypingUpdated(channel, args.SenderSession.Name, msg.Typing);

            foreach (var admin in GetTargetAdmins())
            {
                if (admin.UserId == args.SenderSession.UserId)
                    continue;

                RaiseNetworkEvent(update, admin);
            }
        }

        private void OnServerNameChanged(string obj)
        {
            _serverName = obj;
        }

        private async void OnWebhookChanged(string url)
        {
            _webhookUrl = url;

            RaiseNetworkEvent(new BwoinkDiscordRelayUpdated(!string.IsNullOrWhiteSpace(url)));

            if (url == string.Empty)
                return;

            // Basic sanity check and capturing webhook ID and token
            var match = DiscordRegex().Match(url);

            if (!match.Success)
            {
                // TODO: Ideally, CVar validation during setting should be better integrated
                Log.Warning("Webhook URL does not appear to be valid. Using anyways...");
                _webhookData = await GetWebhookData(url); // Frontier - Support for Custom URLS, we still want to see if theres Webhook data available
                return;
            }

            if (match.Groups.Count <= 2)
            {
                Log.Error("Could not get webhook ID or token.");
                return;
            }

            // Fire and forget
            _webhookData = await GetWebhookData(url); // Frontier - Support for Custom URLS
        }

        private async Task<WebhookData?> GetWebhookData(string url)
        {
            var response = await _httpClient.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Log(LogLevel.Error,
                    $"Webhook returned bad status code when trying to get webhook data (perhaps the webhook URL is invalid?): {response.StatusCode}\nResponse: {content}");
                return null;
            }

            return JsonSerializer.Deserialize<WebhookData>(content);
        }

        private void OnFooterIconChanged(string url)
        {
            _footerIconUrl = url;
        }

        private void OnAvatarChanged(string url)
        {
            _avatarUrl = url;
        }

        private async void ProcessQueue(NetUserId userId, Queue<DiscordRelayedData> messages)
        {
            // Whether an embed already exists for this player
            var exists = _relayMessages.TryGetValue(userId, out var existingEmbed);

            // Whether the message will become too long after adding these new messages
            var tooLong = exists && messages.Sum(msg => Math.Min(msg.Message.Length, MessageLengthCap) + "\n".Length)
                    + existingEmbed?.Description.Length > DescriptionMax;

            // If there is no existing embed, or it is getting too long, we create a new embed
            if (!exists || tooLong)
            {
                var lookup = await _playerLocator.LookupIdAsync(userId);

                if (lookup == null)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Unable to find player for NetUserId {userId} when sending discord webhook.");
                    _relayMessages.Remove(userId);
                    return;
                }

                var linkToPrevious = string.Empty;

                // If we have all the data required, we can link to the embed of the previous round or embed that was too long
                if (_webhookData is { GuildId: { } guildId, ChannelId: { } channelId })
                {
                    if (tooLong && existingEmbed?.Id != null)
                    {
                        linkToPrevious =
                            $"**[Go to previous embed of this round](https://discord.com/channels/{guildId}/{channelId}/{existingEmbed.Id})**\n";
                    }
                    else if (_oldMessageIds.TryGetValue(userId, out var id) && !string.IsNullOrEmpty(id))
                    {
                        linkToPrevious =
                            $"**[Go to last round's conversation with this player](https://discord.com/channels/{guildId}/{channelId}/{id})**\n";
                    }
                }

                var characterName = _minds.GetCharacterName(userId);
                existingEmbed = new DiscordRelayInteraction()
                {
                    Id = null,
                    CharacterName = characterName,
                    Description = linkToPrevious,
                    Username = lookup.Username,
                    LastRunLevel = _gameTicker.RunLevel,
                };

                _relayMessages[userId] = existingEmbed;
            }

            // Previous message was in another RunLevel, so show that in the embed
            if (existingEmbed!.LastRunLevel != _gameTicker.RunLevel)
            {
                existingEmbed.Description += _gameTicker.RunLevel switch
                {
                    GameRunLevel.PreRoundLobby => "\n\n:arrow_forward: _**Pre-round lobby started**_\n",
                    GameRunLevel.InRound => "\n\n:arrow_forward: _**Round started**_\n",
                    GameRunLevel.PostRound => "\n\n:stop_button: _**Post-round started**_\n",
                    _ => throw new ArgumentOutOfRangeException(nameof(_gameTicker.RunLevel),
                        $"{_gameTicker.RunLevel} was not matched."),
                };

                existingEmbed.LastRunLevel = _gameTicker.RunLevel;
            }

            // If last message of the new batch is SOS then relay it to on-call.
            // ... as long as it hasn't been relayed already.
            var discordMention = messages.Last();
            var onCallRelay = !discordMention.Receivers && !existingEmbed.OnCall;

            // Add available messages to the embed description
            while (messages.TryDequeue(out var message))
            {
                string text;

                // In case someone thinks they're funny
                if (message.Message.Length > MessageLengthCap)
                    text = message.Message[..(MessageLengthCap - TooLongText.Length)] + TooLongText;
                else
                    text = message.Message;

                existingEmbed.Description += $"\n{text}";
            }

            var payload = GeneratePayload(existingEmbed.Description,
                existingEmbed.Username,
                userId.UserId, // Frontier, this is used to identify the players in the webhook
                existingEmbed.CharacterName);

            // If there is no existing embed, create a new one
            // Otherwise patch (edit) it
            if (existingEmbed.Id == null)
            {
                var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                var content = await request.Content.ReadAsStringAsync();
                if (!request.IsSuccessStatusCode)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Discord returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    _relayMessages.Remove(userId);
                    return;
                }

                var id = JsonNode.Parse(content)?["id"];
                if (id == null)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Could not find id in json-content returned from discord webhook: {content}");
                    _relayMessages.Remove(userId);
                    return;
                }

                existingEmbed.Id = id.ToString();
            }
            else
            {
                var request = await _httpClient.PatchAsync($"{_webhookUrl}/messages/{existingEmbed.Id}",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (!request.IsSuccessStatusCode)
                {
                    var content = await request.Content.ReadAsStringAsync();
                    _sawmill.Log(LogLevel.Error,
                        $"Discord returned bad status code when patching message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    _relayMessages.Remove(userId);
                    return;
                }
            }

            _relayMessages[userId] = existingEmbed;

            // Actually do the on call relay last, we just need to grab it before we dequeue every message above.
            if (onCallRelay &&
                _onCallData != null)
            {
                existingEmbed.OnCall = true;
                var roleMention = _config.GetCVar(CCVars.DiscordAhelpMention);

                if (!string.IsNullOrEmpty(roleMention))
                {
                    var message = new StringBuilder();
                    message.AppendLine($"<@&{roleMention}>");
                    message.AppendLine("Unanswered SOS");

                    // Need webhook data to get the correct link for that channel rather than on-call data.
                    if (_webhookData is { GuildId: { } guildId, ChannelId: { } channelId })
                    {
                        message.AppendLine(
                            $"**[Go to ahelp](https://discord.com/channels/{guildId}/{channelId}/{existingEmbed.Id})**");
                    }

                    payload = GeneratePayload(message.ToString(), existingEmbed.Username, userId, existingEmbed.CharacterName);

                    var request = await _httpClient.PostAsync($"{_onCallUrl}?wait=true",
                        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                    var content = await request.Content.ReadAsStringAsync();
                    if (!request.IsSuccessStatusCode)
                    {
                        _sawmill.Log(LogLevel.Error, $"Discord returned bad status code when posting relay message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    }
                }
            }
            else
            {
                existingEmbed.OnCall = false;
            }

            _processingChannels.Remove(userId);
        }

        private WebhookPayload GeneratePayload(string messages, string username, Guid userId, string? characterName = null) // Frontier: added Guid
        {
            // Add character name
            if (characterName != null)
                username += $" ({characterName})";

            // If no admins are online, set embed color to red. Otherwise green
            var color = GetNonAfkAdmins().Count > 0 ? 0x41F097 : 0xFF0000;

            // Limit server name to 1500 characters, in case someone tries to be a little funny
            var serverName = _serverName[..Math.Min(_serverName.Length, 1500)];

            var round = _gameTicker.RunLevel switch
            {
                GameRunLevel.PreRoundLobby => _gameTicker.RoundId == 0
                    ? "pre-round lobby after server restart" // first round after server restart has ID == 0
                    : $"pre-round lobby for round {_gameTicker.RoundId + 1}",
                GameRunLevel.InRound => $"round {_gameTicker.RoundId}",
                GameRunLevel.PostRound => $"post-round {_gameTicker.RoundId}",
                _ => throw new ArgumentOutOfRangeException(nameof(_gameTicker.RunLevel),
                    $"{_gameTicker.RunLevel} was not matched."),
            };

            return new WebhookPayload
            {
                Username = username,
                UserID = userId, // Frontier, this is used to identify the players in the webhook
                AvatarUrl = string.IsNullOrWhiteSpace(_avatarUrl) ? null : _avatarUrl,
                Embeds = new List<WebhookEmbed>
                {
                    new()
                    {
                        Description = messages,
                        Color = color,
                        Footer = new WebhookEmbedFooter
                        {
                            Text = $"{serverName} ({round})",
                            IconUrl = string.IsNullOrWhiteSpace(_footerIconUrl) ? null : _footerIconUrl
                        },
                    },
                },
            };
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            foreach (var userId in _messageQueues.Keys.ToArray())
            {
                if (_processingChannels.Contains(userId))
                    continue;

                var queue = _messageQueues[userId];
                _messageQueues.Remove(userId);
                if (queue.Count == 0)
                    continue;

                _processingChannels.Add(userId);

                ProcessQueue(userId, queue);
            }
        }

        // Frontier: webhook text messages
        public void OnWebhookBwoinkTextMessage(BwoinkTextMessage message, ServerApi.BwoinkActionBody body)
        {
            // Note for forks:
            AdminData webhookAdminData = new();

            var bwoinkParams = new BwoinkParams(
                message,
                SystemUserId,
                webhookAdminData,
                body.Username,
                null,
                body.UserOnly,
                body.WebhookUpdate,
                true,
                body.RoleName,
                body.RoleColor);
            OnBwoinkInternal(bwoinkParams);
        }

        protected override void OnBwoinkTextMessage(BwoinkTextMessage message, EntitySessionEventArgs eventArgs)
        {
            base.OnBwoinkTextMessage(message, eventArgs);

            var senderSession = eventArgs.SenderSession;

            // TODO: Sanitize text?
            // Confirm that this person is actually allowed to send a message here.
            var personalChannel = senderSession.UserId == message.UserId;
            var senderAdmin = _adminManager.GetAdminData(senderSession);
            var senderAHelpAdmin = senderAdmin?.HasFlag(AdminFlags.Adminhelp) ?? false;
            var authorized = personalChannel || senderAHelpAdmin;
            if (!authorized)
            {
                // Unauthorized bwoink (log?)
                return;
            }

            if (_rateLimit.CountAction(eventArgs.SenderSession, RateLimitKey) != RateLimitStatus.Allowed)
                return;

            var bwoinkParams = new BwoinkParams(message,
                eventArgs.SenderSession.UserId,
                senderAdmin,
                eventArgs.SenderSession.Name,
                eventArgs.SenderSession.Channel,
                false,
                true,
                false);
            OnBwoinkInternal(bwoinkParams);
        }

        /// <summary>
        /// Sends a bwoink. Common to both internal messages (sent via the ahelp or admin interface) and webhook messages (sent through the webhook, e.g. via Discord)
        /// </summary>
        /// <param name="bwoinkParams">The parameters of the message being sent.</param>
        private void OnBwoinkInternal(BwoinkParams bwoinkParams)
        {
            _activeConversations[bwoinkParams.Message.UserId] = DateTime.Now;

            var escapedText = FormattedMessage.EscapeText(bwoinkParams.Message.Text);
            var adminColor = _config.GetCVar(CCVars.AdminBwoinkColor);
            var adminPrefix = "";
            var bwoinkText = $"{bwoinkParams.SenderName}";

            //Getting an administrator position
            if (_config.GetCVar(CCVars.AhelpAdminPrefix))
            {
                if (bwoinkParams.SenderAdmin is not null && bwoinkParams.SenderAdmin.Title is not null)
                    adminPrefix = $"[bold]\\[{bwoinkParams.SenderAdmin.Title}\\][/bold] ";

                if (_config.GetCVar(CCVars.UseDiscordRoleName) && bwoinkParams.RoleName is not null)
                    adminPrefix = $"[bold]\\[{bwoinkParams.RoleName}\\][/bold] ";
            }

            if (!bwoinkParams.FromWebhook
                && _config.GetCVar(CCVars.UseAdminOOCColorInBwoinks)
                && bwoinkParams.SenderAdmin is not null)
            {
                var prefs = _preferencesManager.GetPreferences(bwoinkParams.SenderId);
                adminColor = prefs.AdminOOCColor.ToHex();
            }

            // If role color is enabled and exists, use it, otherwise use the discord reply color
            if (_config.GetCVar(CCVars.DiscordReplyColor) != string.Empty && bwoinkParams.FromWebhook)
                adminColor = _config.GetCVar(CCVars.DiscordReplyColor);

            if (_config.GetCVar(CCVars.UseDiscordRoleColor) && bwoinkParams.RoleColor is not null)
                adminColor = bwoinkParams.RoleColor;

            if (bwoinkParams.SenderAdmin is not null)
            {
                if (bwoinkParams.SenderAdmin.Flags ==
                    AdminFlags.Adminhelp) // Mentor. Not full admin. That's why it's colored differently.
                    bwoinkText = $"[color=purple]{adminPrefix}{bwoinkParams.SenderName}[/color]";
                else if (bwoinkParams.FromWebhook || bwoinkParams.SenderAdmin.HasFlag(AdminFlags.Adminhelp)) // Frontier: anything sent via webhooks are from an admin.
                    bwoinkText = $"[color={adminColor}]{adminPrefix}{bwoinkParams.SenderName}[/color]";
            }

            if (bwoinkParams.FromWebhook)
                bwoinkText = $"{_config.GetCVar(CCVars.DiscordReplyPrefix)}{bwoinkText}";

            bwoinkText = $"{(bwoinkParams.Message.PlaySound ? "" : "(S) ")}{bwoinkText}: {escapedText}";

            // If it's not an admin / admin chooses to keep the sound then play it.
            var playSound = bwoinkParams.SenderAdmin == null || bwoinkParams.Message.PlaySound;
            var msg = new BwoinkTextMessage(bwoinkParams.Message.UserId, bwoinkParams.SenderId, bwoinkText, playSound: playSound);

            LogBwoink(msg);

            var admins = GetTargetAdmins();

            // Notify all admins
            if (!bwoinkParams.UserOnly)
            {
                foreach (var channel in admins)
                {
                    RaiseNetworkEvent(msg, channel);
                }
            }

            string adminPrefixWebhook = "";

            if (_config.GetCVar(CCVars.AhelpAdminPrefixWebhook) && bwoinkParams.SenderAdmin is not null && bwoinkParams.SenderAdmin.Title is not null)
            {
                adminPrefixWebhook = $"[bold]\\[{bwoinkParams.SenderAdmin.Title}\\][/bold] ";
            }

            // Notify player
            if (_playerManager.TryGetSessionById(bwoinkParams.Message.UserId, out var session))
            {
                if (!admins.Contains(session.Channel))
                {
                    // If _overrideClientName is set, we generate a new message with the override name. The admins name will still be the original name for the webhooks.
                    if (_overrideClientName != string.Empty)
                    {
                        string overrideMsgText;
                        // Doing the same thing as above, but with the override name. Theres probably a better way to do this.
                        if (bwoinkParams.SenderAdmin is not null &&
                            bwoinkParams.SenderAdmin.Flags ==
                            AdminFlags.Adminhelp) // Mentor. Not full admin. That's why it's colored differently.
                            overrideMsgText = $"[color=purple]{adminPrefixWebhook}{_overrideClientName}[/color]";
                        else if (bwoinkParams.SenderAdmin is not null && bwoinkParams.SenderAdmin.HasFlag(AdminFlags.Adminhelp))
                            overrideMsgText = $"[color=red]{adminPrefixWebhook}{_overrideClientName}[/color]";
                        else
                            overrideMsgText = $"{bwoinkParams.SenderName}"; // Not an admin, name is not overridden.

                        if (bwoinkParams.FromWebhook)
                            overrideMsgText = $"{_config.GetCVar(CCVars.DiscordReplyPrefix)}{overrideMsgText}";

                        overrideMsgText = $"{(bwoinkParams.Message.PlaySound ? "" : "(S) ")}{overrideMsgText}: {escapedText}";

                        RaiseNetworkEvent(new BwoinkTextMessage(bwoinkParams.Message.UserId,
                                bwoinkParams.SenderId,
                                overrideMsgText,
                                playSound: playSound),
                            session.Channel);
                    }
                    else
                        RaiseNetworkEvent(msg, session.Channel);
                }
            }

            var sendsWebhook = _webhookUrl != string.Empty;
            if (sendsWebhook && bwoinkParams.SendWebhook)
            {
                if (!_messageQueues.ContainsKey(msg.UserId))
                    _messageQueues[msg.UserId] = new Queue<DiscordRelayedData>();

                var str = bwoinkParams.Message.Text;
                var unameLength = bwoinkParams.SenderName.Length;

                if (unameLength + str.Length + _maxAdditionalChars > DescriptionMax)
                {
                    str = str[..(DescriptionMax - _maxAdditionalChars - unameLength)];
                }

                var nonAfkAdmins = GetNonAfkAdmins();
                var messageParams = new AHelpMessageParams(
                    bwoinkParams.SenderName,
                    str,
                    bwoinkParams.SenderId != bwoinkParams.Message.UserId,
                    _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                    _gameTicker.RunLevel,
                    playedSound: playSound,
                    isDiscord: bwoinkParams.FromWebhook,
                    noReceivers: nonAfkAdmins.Count == 0
                );
                _messageQueues[msg.UserId].Enqueue(GenerateAHelpMessage(messageParams));
            }

            if (admins.Count != 0 || sendsWebhook)
                return;

            // No admin online, let the player know
            if (bwoinkParams.SenderChannel != null)
            {
                var systemText = Loc.GetString("bwoink-system-starmute-message-no-other-users");
                var starMuteMsg = new BwoinkTextMessage(bwoinkParams.Message.UserId, SystemUserId, systemText);
                RaiseNetworkEvent(starMuteMsg, bwoinkParams.SenderChannel);
            }
        }
        // End Frontier:

        private IList<INetChannel> GetNonAfkAdmins()
        {
            return _adminManager.ActiveAdmins
                .Where(p => (_adminManager.GetAdminData(p)?.HasFlag(AdminFlags.Adminhelp) ?? false) &&
                            !_afkManager.IsAfk(p))
                .Select(p => p.Channel)
                .ToList();
        }

        private IList<INetChannel> GetTargetAdmins()
        {
            return _adminManager.ActiveAdmins
                .Where(p => _adminManager.GetAdminData(p)?.HasFlag(AdminFlags.Adminhelp) ?? false)
                .Select(p => p.Channel)
                .ToList();
        }

        private static DiscordRelayedData GenerateAHelpMessage(AHelpMessageParams parameters)
        {
            var stringbuilder = new StringBuilder();

            if (parameters.Icon != null)
                stringbuilder.Append(parameters.Icon);
            else if (parameters.IsAdmin)
                stringbuilder.Append(":outbox_tray:");
            else if (parameters.NoReceivers)
                stringbuilder.Append(":sos:");
            else
                stringbuilder.Append(":inbox_tray:");

            if (parameters.RoundTime != string.Empty && parameters.RoundState == GameRunLevel.InRound)
                stringbuilder.Append($" **{parameters.RoundTime}**");
            if (!parameters.PlayedSound)
                stringbuilder.Append(" **(S)**");

            if (parameters.IsDiscord) // Frontier - Discord Indicator
                stringbuilder.Append(" **(DC)**");

            if (parameters.Icon == null)
                stringbuilder.Append($" **{parameters.Username}:** ");
            else
                stringbuilder.Append($" **{parameters.Username}** ");
            stringbuilder.Append(parameters.Message);

            return new DiscordRelayedData()
            {
                Receivers = !parameters.NoReceivers,
                Message = stringbuilder.ToString(),
            };
        }

        private record struct DiscordRelayedData
        {
            /// <summary>
            /// Was anyone online to receive it.
            /// </summary>
            public bool Receivers;

            /// <summary>
            /// What's the payload to send to discord.
            /// </summary>
            public string Message;
        }

        /// <summary>
        ///  Class specifically for holding information regarding existing Discord embeds
        /// </summary>
        private sealed class DiscordRelayInteraction
        {
            public string? Id;

            public string Username = String.Empty;

            public string? CharacterName;

            /// <summary>
            /// Contents for the discord message.
            /// </summary>
            public string Description = string.Empty;

            /// <summary>
            /// Run level of the last interaction. If different we'll link to the last Id.
            /// </summary>
            public GameRunLevel LastRunLevel;

            /// <summary>
            /// Did we relay this interaction to OnCall previously.
            /// </summary>
            public bool OnCall;
        }
    }

    public sealed class AHelpMessageParams
    {
        public string Username { get; set; }
        public string Message { get; set; }
        public bool IsAdmin { get; set; }
        public string RoundTime { get; set; }
        public GameRunLevel RoundState { get; set; }
        public bool PlayedSound { get; set; }
        public bool NoReceivers { get; set; }
        public bool IsDiscord { get; set; } // Frontier
        public string? Icon { get; set; }

        public AHelpMessageParams(
            string username,
            string message,
            bool isAdmin,
            string roundTime,
            GameRunLevel roundState,
            bool playedSound,
            bool isDiscord = false, // Frontier
            bool noReceivers = false,
            string? icon = null)
        {
            Username = username;
            Message = message;
            IsAdmin = isAdmin;
            RoundTime = roundTime;
            RoundState = roundState;
            IsDiscord = isDiscord; // Frontier
            PlayedSound = playedSound;
            NoReceivers = noReceivers;
            Icon = icon;
        }
    }

    public sealed class BwoinkParams
    {
        public SharedBwoinkSystem.BwoinkTextMessage Message { get; set; }
        public NetUserId SenderId { get; set; }
        public AdminData? SenderAdmin { get; set; }
        public string SenderName { get; set; }
        public INetChannel? SenderChannel { get; set; }
        public bool UserOnly { get; set; }
        public bool SendWebhook { get; set; }
        public bool FromWebhook { get; set; }
        public string? RoleName { get; set; }
        public string? RoleColor { get; set; }

        public BwoinkParams(
            SharedBwoinkSystem.BwoinkTextMessage message,
            NetUserId senderId,
            AdminData? senderAdmin,
            string senderName,
            INetChannel? senderChannel,
            bool userOnly,
            bool sendWebhook,
            bool fromWebhook,
            string? roleName = null,
            string? roleColor = null)
        {
            Message = message;
            SenderId = senderId;
            SenderAdmin = senderAdmin;
            SenderName = senderName;
            SenderChannel = senderChannel;
            UserOnly = userOnly;
            SendWebhook = sendWebhook;
            FromWebhook = fromWebhook;
            RoleName = roleName;
            RoleColor = roleColor;
        }
    }

    public enum PlayerStatusType
    {
        Connected,
        Disconnected,
        Banned,
    }
}
