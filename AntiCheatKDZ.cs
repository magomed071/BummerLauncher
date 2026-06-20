using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Plugins;
using UnityEngine;
using WebSocketSharp;
using Network;

namespace Oxide.Plugins
{
    [Info("AntiCheatKDZ", "Kaidoz", "0.1")]
    [Description("Anti-cheat plugin for Rust server")]
    public class AntiCheatKDZ : RustPlugin
    {
        #region Configuration

        private ConfigModel config;

        private class ConfigModel
        {
            public bool CheckOnlyDbExists { get; set; }
            public string ApiKey { get; set; }
            public List<ulong> ExcludeSteamIds { get; set; }
            public bool AllowConnectionWhenOffline { get; set; }
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigModel
            {
                CheckOnlyDbExists = false,
                ApiKey = "6546546456456",
                ExcludeSteamIds = new List<ulong> { 76561198874259939 },
                AllowConnectionWhenOffline = false
            };
            SaveConfig();
        }

        private new void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region Lang

        private string DisconnectMessage = "<color=#ad3e4b><b>Для игры на наших серверах, вам нужно запускать игру через BummerLauncher!\nЕсли у вас его нету скачивайте обновление по ссылке - https://cdn.bummerrust.ru/download/updater.rar</b></color>";
        private string BanMessage = "<color=#ad3e4b><b>Вы навсегда заблокированы на всех наших серверах. \nПричина: Использавание запрещенного ПО!\nДля покупки разбана создавайте обращение в дискорд канале - discord.gg/bummerrust</b></color>\n";
        private string AntiCheatOfflineMessage = "<color=#ad3e4b><b>Система античита временно недоступна. Попробуйте подключиться позже.</b></color>";

        #endregion

        #region Logging

        private string logFilePath => Path.Combine(Interface.Oxide.LogDirectory, "AntiCheatKDZ.log");

        private void Log(string message, bool isError = false)
        {
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(isError ? "ERROR: " : "")}{message}";
            
            // Запись в файл лога
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine(logMessage);
            }
            
            // Вывод только критических ошибок в консоль
            if (isError)
            {
                Puts(logMessage);
            }
        }

        private void LogWebSocketEvent(string eventType, string details)
        {
            Log($"WebSocket {eventType}: {details}");
        }

        private void LogPlayerEvent(ulong steamId, string eventType, string details = "")
        {
            Log($"Player {steamId} {eventType}{(string.IsNullOrEmpty(details) ? "" : $": {details}")}");
        }

        private void LogBanEvent(ulong steamId, string reason, DateTime? expireDate = null)
        {
            string dateInfo = expireDate.HasValue ? $" until {expireDate.Value:yyyy-MM-dd HH:mm}" : "permanently";
            Log($"BAN: Player {steamId} banned {dateInfo}. Reason: {reason}");
        }

        #endregion

        #region WebSocket

        private WebSocket socketServer;
        private bool IsConnected = false;
        private readonly object _socketLock = new object();
        private int reconnectAttempts = 0;
        private int MaxReconnectAttempts = 100;

       private void ConnectToWebSocket()
{
    try
    {
        LogWebSocketEvent("connection", "Attempting to connect...");
        
        socketServer = new WebSocket("ws://anticheat.bummerrust.ru:2290");

        socketServer.OnOpen += OnOpen;
        socketServer.OnMessage += OnMessage;
        socketServer.OnClose += OnClose;
        socketServer.OnError += OnError;

        socketServer.ConnectAsync();
    }
    catch (Exception ex)
    {
        Log($"WebSocket connection error: {ex.Message}", true);
        Reconnect();
    }
}

private void OnOpen(object sender, EventArgs e)
{
    IsConnected = true;
    reconnectAttempts = 0;
    _isReconnecting = false;
    LogWebSocketEvent("connection", "Connected successfully");
    SendMessageAsync(AuthCommand, config.ApiKey);
}

private void OnMessage(object sender, MessageEventArgs e)
{
    try
    {
        // Добавить уникальный идентификатор для отслеживания
        string messageId = Guid.NewGuid().ToString("N").Substring(0, 8);
        LogWebSocketEvent("message", $"Received [{messageId}]: {e.Data}");
        HandleMessage(e.Data);
    }
    catch (Exception ex)
    {
        Log($"Error processing WebSocket message: {ex.Message}", true);
    }
}

private void OnClose(object sender, CloseEventArgs e)
{
    IsConnected = false;
    LogWebSocketEvent("disconnection", $"Closed. Code: {e.Code}, Reason: {e.Reason}");
    Reconnect();
}

private new void OnError(object sender, WebSocketSharp.ErrorEventArgs e)
{
    Log($"WebSocket error: {e.Message}", true);
    Reconnect();
}

        private bool _isReconnecting = false;
        
        private void Reconnect()
        {
            // Защита от одновременных попыток реконнекта
            if (_isReconnecting)
            {
                Log("Reconnect already in progress, skipping");
                return;
            }
            
            if (reconnectAttempts >= MaxReconnectAttempts)
            {
                Log("Max reconnect attempts reached. Please check the WebSocket server.", true);
                return;
            }

            _isReconnecting = true;
            int delay = Math.Min(60, (int)Math.Pow(2, Math.Min(reconnectAttempts, 6))); // Макс 64 сек
            reconnectAttempts++;

            timer.Once(delay, () =>
            {
                try
                {
                    if (socketServer != null && (socketServer.ReadyState == WebSocketState.Closed || socketServer.ReadyState == WebSocketState.Closing))
                    {
                        LogWebSocketEvent("reconnection", $"Attempt {reconnectAttempts}...");
                        ConnectToWebSocket();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Reconnection error: {ex.Message}", true);
                }
                finally
                {
                    _isReconnecting = false;
                }
            });
        }

private Task SendMessageAsync(string command, string value) 
{ 
    try 
    { 
        // Если нет подключения — не отправляем и не триггерим кик
        if (!IsConnected || socketServer == null || socketServer.ReadyState != WebSocketState.Open) 
        { 
            Log($"WebSocket not connected — message '{command}' for '{value}' skipped");
            return Task.CompletedTask; 
        } 

        lock (_socketLock) 
        { 
            string message = command + ":" + value; 
            socketServer.Send(message); 
            Log($"Sent message: {message}"); 
        } 
    } 
    catch (Exception ex) 
    { 
        Log($"Error sending message: {ex.Message}", true); 
        if (ex is WebSocketException || ex is InvalidOperationException) 
        { 
            Reconnect(); 
        } 
    } 
    
    return Task.CompletedTask;
}

        #endregion

        #region Player Handling

        private List<Connection> Connections = new List<Connection>();
        private Dictionary<ulong, string> SteamIdsForKick = new Dictionary<ulong, string>();
        private Dictionary<ulong, TaskCompletionSource<bool>> _pendingChecks = new Dictionary<ulong, TaskCompletionSource<bool>>();
        private float CheckTimeout = 10f; // Таймаут ожидания ответа от античита в секундах

        private void Init()
        {
            try
            {
                SteamIdsForKick = new Dictionary<ulong, string>();
                _processedConnections = new HashSet<ulong>();
                _disconnectScheduled = new HashSet<ulong>();
                _playerConnectAttempts = new Dictionary<ulong, int>();
                _pendingChecks = new Dictionary<ulong, TaskCompletionSource<bool>>();
                config = Config.ReadObject<ConfigModel>();
                Log("Plugin initialized");
            }
            catch (Exception ex)
            {
                Log($"Initialization error: {ex.Message}", true);
            }
        }

        private void OnServerInitialized()
        {
            try
            {
                ConnectToWebSocket();
                Log("Server initialized, WebSocket connection started");
            }
            catch (Exception ex)
            {
                Log($"Server initialization error: {ex.Message}", true);
            }
        }

        private async void OnClientAuth(Connection connection)
        {
            if (connection == null) return;
            
            try
            {
                await CheckConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"CRITICAL: Error in OnClientAuth for {connection.userid}: {ex.Message}\n{ex.StackTrace}", true);
                // Не кикаем игрока при ошибке плагина - просто логируем
            }
        }

        private Dictionary<ulong, int> _playerConnectAttempts = new Dictionary<ulong, int>();
        
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            try
            {
                if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
                {
                    // Защита от бесконечной рекурсии
                    if (!_playerConnectAttempts.ContainsKey(player.userID))
                        _playerConnectAttempts[player.userID] = 0;
                    
                    _playerConnectAttempts[player.userID]++;
                    
                    if (_playerConnectAttempts[player.userID] > 10)
                    {
                        Log($"Player {player.userID} exceeded reconnect attempts limit", true);
                        _playerConnectAttempts.Remove(player.userID);
                        return;
                    }
                    
                    timer.Once(2f, () => OnPlayerConnected(player));
                    return;
                }
                
                // Очистка счётчика при успешном подключении
                if (_playerConnectAttempts.ContainsKey(player.userID))
                    _playerConnectAttempts.Remove(player.userID);

                string reason;
                if (SteamIdsForKick.TryGetValue(player.userID, out reason))
                {
                    LogPlayerEvent(player.userID, "kicked", reason);
                    player.Kick(reason);
                    SteamIdsForKick.Remove(player.userID);
                }
            }
            catch (Exception ex)
            {
                Log($"Error in OnPlayerConnected for {player.userID}: {ex.Message}", true);
                // Очистка при ошибке
                if (_playerConnectAttempts.ContainsKey(player.userID))
                    _playerConnectAttempts.Remove(player.userID);
            }
        }
        
        private HashSet<ulong> _processedConnections = new HashSet<ulong>();

        private async Task CheckConnectionAsync(Connection connection)
{
    if (connection == null) return;

    // Защита от повторной обработки
    if (_processedConnections.Contains(connection.userid))
    {
        LogPlayerEvent(connection.userid, "already processed, skipping");
        return;
    }
    
    _processedConnections.Add(connection.userid);

    try
    {
        // Проверка исключений из античита
        if (config.ExcludeSteamIds.Contains(connection.userid))
        {
            LogPlayerEvent(connection.userid, "excluded from checks");
            return;
        }

        // ИСПРАВЛЕНИЕ: Проверяем подключение и создаем ожидание ответа
        if (!IsConnected)
        {
            if (config.AllowConnectionWhenOffline)
            {
                LogPlayerEvent(connection.userid, "allowed connection (anti-cheat offline, AllowConnectionWhenOffline=true)");
                return;
            }
            else
            {
                LogPlayerEvent(connection.userid, "rejected (anti-cheat offline)", true);
                DisconnectConnection(connection.userid, AntiCheatOfflineMessage);
                return;
            }
        }

        if (SteamIdsForKick.ContainsKey(connection.userid))
            SteamIdsForKick.Remove(connection.userid);

        var existingConnection = Connections.FirstOrDefault(con => con.userid == connection.userid);
        if (existingConnection != null)
            Connections.Remove(existingConnection);

        Connections.Add(connection);

        // Создаем TaskCompletionSource для ожидания ответа
        var tcs = new TaskCompletionSource<bool>();
        _pendingChecks[connection.userid] = tcs;

        // Отправляем запрос
        if (config.CheckOnlyDbExists)
        {
            LogPlayerEvent(connection.userid, "check in database");
            await SendMessageAsync(ExistsCommand, connection.userid.ToString());
        }
        else
        {
            string steamId = connection.userid.ToString();
            string ipAddress = GetCorrectIp(connection.ipaddress);
            LogPlayerEvent(connection.userid, "checked", $"IP: {ipAddress}");
            await SendMessageAsync(ConnectedCommand, steamId + ":" + ipAddress);
        }

        // Проверяем, что сообщение реально отправилось
        if (!IsConnected)
        {
            LogPlayerEvent(connection.userid, "rejected (connection lost during check)", true);
            DisconnectConnection(connection.userid, AntiCheatOfflineMessage);
            _pendingChecks.Remove(connection.userid);
            RemoveCache(connection.userid);
            return;
        }

        // Автоматически разрешаем вход через 3 секунды, если античит не прислал команду на кик
        timer.Once(3f, () => 
        {
            if (_pendingChecks.ContainsKey(connection.userid))
            {
                LogPlayerEvent(connection.userid, "auto-approved (no reject from anti-cheat)");
                CompletePendingCheck(connection.userid, true);
            }
        });

        // Ждем ответ от античита с общим таймаутом
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(CheckTimeout));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            // Таймаут - кикаем игрока (античит вообще не отвечает)
            LogPlayerEvent(connection.userid, "rejected (anti-cheat timeout)", true);
            DisconnectConnection(connection.userid, AntiCheatOfflineMessage);
        }
        else
        {
            // Получили ответ
            bool allowed = await tcs.Task;
            if (!allowed)
            {
                // Античит запретил вход - игрок уже кикнут через HandleDisconnect/HandleKick
                LogPlayerEvent(connection.userid, "rejected by anti-cheat");
            }
            else
            {
                LogPlayerEvent(connection.userid, "approved by anti-cheat");
            }
        }

        // Очистка
        _pendingChecks.Remove(connection.userid);
        RemoveCache(connection.userid);
    }
    catch (Exception ex)
    {
        Log($"Error in CheckConnectionAsync for {connection.userid}: {ex.Message}", true);
        // При ошибке кикаем для безопасности
        DisconnectConnection(connection.userid, AntiCheatOfflineMessage);
        _pendingChecks.Remove(connection.userid);
    }
    finally
    {
        // Удаляем из обработанных через некоторое время
        timer.Once(5f, () => _processedConnections.Remove(connection.userid));
    }
}

        private static string GetCorrectIp(string ip)
        {
            try
            {
                return ip.Substring(0, ip.IndexOf(":"));
            }
            catch
            {
                return ip;
            }
        }

        private void RemoveCache(ulong steamId)
        {
            timer.Once(1f, () =>
            {
                try
                {
                    var connection = Connections.FirstOrDefault(con => con.userid == steamId);
                    if (connection != null)
                        Connections.Remove(connection);
                }
                catch (Exception ex)
                {
                    Log($"Error in RemoveCache for {steamId}: {ex.Message}", true);
                }
            });
        }

        #endregion

        #region Command Handling

        private const string AuthCommand = "auth";
        private const string BanCommand = "ban";
        private const string ExistsCommand = "exists";
        private const string KickCommand = "kick";
        private const string ConnectedCommand = "connected";
        private const string DisconnectedCommand = "disconnected";

        private void HandleMessage(string message)
        {
            try
            {
                var splits = message.Split(':');
                if (splits.Length <= 1)
                {
                    Log($"Received malformed message: {message}");
                    return;
                }

                string command = splits[0];
                var values = splits.Skip(1).ToArray();

                HandleCommand(command, values);
            }
            catch (Exception ex)
            {
                Log($"Error handling message: {ex.Message}", true);
            }
        }

private void HandleCommand(string command, string[] values)
{
    try
    {
        switch (command)
        {
            case DisconnectedCommand:
                HandleDisconnect(values);
                break;
            case BanCommand:
                HandleBan(values);
                break;
            case KickCommand:
                HandleKick(values);
                break;
            case "DISCONNECT_MESSAGE":
                HandleDisconnect(values);
                break;
            case "allowed":
            case "approved":
                // Античит разрешил вход
                if (values.Length > 0 && ulong.TryParse(values[0], out ulong allowedId))
                {
                    CompletePendingCheck(allowedId, true);
                }
                break;
            default:
                Log($"Unknown command received: {command}");
                break;
        }
    }
    catch (Exception ex)
    {
        Log($"Error handling command {command}: {ex.Message}", true);
    }
}

private void CompletePendingCheck(ulong steamId, bool allowed)
{
    try
    {
        if (_pendingChecks.TryGetValue(steamId, out var tcs))
        {
            tcs.TrySetResult(allowed);
        }
    }
    catch (Exception ex)
    {
        Log($"Error in CompletePendingCheck for {steamId}: {ex.Message}", true);
    }
}

// Добавить в начало класса, рядом с другими полями
private HashSet<ulong> _disconnectScheduled = new HashSet<ulong>();

// В методе HandleDisconnect добавить логирование
private void HandleDisconnect(string[] values)
{
    if (values.Length < 1) return;

    try
    {
        ulong steamId = ulong.Parse(values[0]);
        
        // Завершаем проверку с отказом
        CompletePendingCheck(steamId, false);
        
        // Защита от повторной обработки
        if (_disconnectScheduled.Contains(steamId))
        {
            LogPlayerEvent(steamId, "disconnect already scheduled, skipping");
            return;
        }
        
        _disconnectScheduled.Add(steamId);
        LogPlayerEvent(steamId, $"scheduled for disconnect (HashSet count: {_disconnectScheduled.Count})");
        
        timer.Once(5f, () => 
        {
            DisconnectConnection(steamId, DisconnectMessage);
            _disconnectScheduled.Remove(steamId);
            LogPlayerEvent(steamId, "disconnect completed and removed from HashSet");
        });
    }
    catch (Exception ex)
    {
        Log($"Error in HandleDisconnect: {ex.Message}", true);
    }
}

        private void HandleBan(string[] values)
        {
            if (values.Length < 3) return;

            try
            {
                ulong steamId = ulong.Parse(values[0]);
                string reason = values[1];
                string strDate = string.Join(":", values.Skip(2));
                var date = DateTime.ParseExact(strDate, "dd:MM:yyyy HH:mm", null).AddHours(3);

                // Завершаем проверку с отказом
                CompletePendingCheck(steamId, false);

                LogBanEvent(steamId, reason, date);
                BanConnection(steamId, reason, date);
            }
            catch (Exception ex)
            {
                Log($"Error in HandleBan: {ex.Message}", true);
            }
        }

        private void HandleKick(string[] values)
        {
            if (values.Length < 2) return;

            try
            {
                ulong steamId = ulong.Parse(values[0]);
                string reason = values[1];

                // Завершаем проверку с отказом
                CompletePendingCheck(steamId, false);

                LogPlayerEvent(steamId, "kicked", reason);
                DisconnectConnection(steamId, reason);
            }
            catch (Exception ex)
            {
                Log($"Error in HandleKick: {ex.Message}", true);
            }
        }

        private void BanConnection(ulong steamId, string reason, DateTime expireDateTime)
        {
            try
            {
                string formattedReason = BanMessage
                    .Replace("%reason%", reason)
                    .Replace("%date%", expireDateTime.ToString("dd:MM:yyyy HH:mm"));

                DisconnectConnection(steamId, formattedReason);
            }
            catch (Exception ex)
            {
                Log($"Error in BanConnection for {steamId}: {ex.Message}", true);
            }
        }

        private void DisconnectConnection(ulong steamId, string reason)
        {
            try
            {
                var connection = Connections.FirstOrDefault(con => con.userid == steamId);
                if (connection != null)
                {
                    ConnectionAuth.Reject(connection, reason);
                    Connections.Remove(connection);
                }

                var netConnection = Net.sv.connections.FirstOrDefault(con => con.userid == steamId);
                if (netConnection != null)
                {
                    Net.sv.Kick(netConnection, reason);
                }

                if (!SteamIdsForKick.ContainsKey(steamId))
                    SteamIdsForKick.Add(steamId, reason);

                var player = BasePlayer.FindByID(steamId);
                if (player != null)
                {
                    player.Kick(reason);
                }
            }
            catch (Exception ex)
            {
                Log($"Error disconnecting {steamId}: {ex.Message}", true);
            }
        }

        #endregion

        #region Console Commands

        [ConsoleCommand("anticheatclient.ignore")]
        private void ScreenHandler(ConsoleSystem.Arg args)
        {
            if (args.Player() == null && !args.IsAdmin) return;

            try
            {
                if (args.Args.Length < 2)
                {
                    Log("Invalid command usage: anticheatclient.ignore <add|remove> <steamId>");
                    return;
                }

                string action = args.Args[0];
                ulong steamId = Convert.ToUInt64(args.Args[1]);

                switch (action)
                {
                    case "add":
                        config.ExcludeSteamIds.Add(steamId);
                        Log($"Added {steamId} to exclude list");
                        break;
                    case "remove":
                        config.ExcludeSteamIds.Remove(steamId);
                        Log($"Removed {steamId} from exclude list");
                        break;
                    default:
                        Log($"Invalid action: {action}");
                        break;
                }

                SaveConfig();
            }
            catch (Exception ex)
            {
                Log($"Error in ScreenHandler command: {ex.Message}", true);
            }
        }

        #endregion

        #region Unload

        void Unload()
        {
            Log("Plugin unloading...");
            
            // Останавливаем реконнекты
            reconnectAttempts = MaxReconnectAttempts;
            _isReconnecting = false;
            
            if (socketServer != null)
            {
                try
                {
                    socketServer.OnOpen -= OnOpen;
                    socketServer.OnMessage -= OnMessage;
                    socketServer.OnClose -= OnClose;
                    socketServer.OnError -= OnError;

                    if (socketServer.ReadyState == WebSocketState.Open || socketServer.ReadyState == WebSocketState.Connecting)
                    {
                        socketServer.Close(CloseStatusCode.Normal, "Plugin unloading");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error while closing WebSocket: {ex.Message}", true);
                }
                finally
                {
                    socketServer = null;
                }
            }

            // Очищаем все коллекции
            Connections?.Clear();
            SteamIdsForKick?.Clear();
            _processedConnections?.Clear();
            _disconnectScheduled?.Clear();
            _playerConnectAttempts?.Clear();
            _pendingChecks?.Clear();
            
            Log("Plugin unloaded successfully");
        }

        #endregion
    }
}
