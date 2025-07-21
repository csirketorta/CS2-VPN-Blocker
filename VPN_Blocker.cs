using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Admin; // Needed for AdminManager
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPNBlockerPlugin
{
    public class VPNBlockerConfig
    {
        public int MonitorMode { get; set; } = 1; // 1 = log only, 0 = enforce kicks
        public string DbHost { get; set; } = "127.0.0.1";
        public string DbUser { get; set; } = "username";
        public string DbPass { get; set; } = "password";
        public string DbName { get; set; } = "your_database";
        public string VpnApiUrl { get; set; } = "http://v2.api.iphub.info/ip/{0}";
        public string IpHubApiKey { get; set; } = "YOUR_API_KEY_HERE";
        public int CacheDays { get; set; } = 180;

        // Add whitelist support
        public List<string> WhitelistedIPs { get; set; } = new List<string>();
        public List<string> WhitelistedSteamIDs { get; set; } = new List<string>();
    }

    public class VPNBlocker : BasePlugin
    {
        public override string ModuleName => "VPN Blocker";
        public override string ModuleVersion => "1.1.6";
        public override string ModuleAuthor => "csirk";

        private VPNBlockerConfig _c;
        private readonly Dictionary<string, bool> vpnCache = new();
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly Dictionary<string, string> playerInitialIPs = new();
        private readonly HashSet<string> alreadyReportedVPN = new();


        public override void Load(bool hotReload)
        {
            Logger.LogInformation("[VPNBlocker] Loading plugin...");
            LoadConfig();

            RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            Logger.LogInformation("[VPNBlocker] Registered EventPlayerConnectFull.");

        }


        private void LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "vpnblocker_config.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                _c = JsonSerializer.Deserialize<VPNBlockerConfig>(json);
                Logger.LogInformation("[VPNBlocker] Config loaded successfully.");
            }
            else
            {
                _c = new VPNBlockerConfig();
                string json = JsonSerializer.Serialize(_c, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                Logger.LogWarning("[VPNBlocker] Config file created. Please edit vpnblocker_config.json and restart.");
            }
        }

        private bool IsAdmin(CCSPlayerController player)
        {
            // "css/generic" is the general permission
            return AdminManager.PlayerHasPermissions(player, "@css/generic");
        }

        private void NotifyAdmins(string message)
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && IsAdmin(p))
                {
                    p.PrintToChat($" \x04[VPNBlocker]\x01 {message}");
                }
            }
        }

        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            Logger.LogInformation("[VPNBlocker] Round start - checking for mid-game IP changes");

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid)
                    continue;

                string currentIp = player.IpAddress?.Split(':')[0];
                string steamId = player.SteamID.ToString();
                string playerName = player.PlayerName ?? "Unknown";

                // Skip whitelist & private IPs
                if (_c.WhitelistedIPs.Contains(currentIp) || _c.WhitelistedSteamIDs.Contains(steamId))
                    continue;
                if (IsLocalOrPrivateIP(currentIp))
                    continue;

                // Detect mid-game IP change
                if (playerInitialIPs.TryGetValue(steamId, out var originalIp))
                {
                    // Only proceed if IP actually changed
                    if (originalIp != currentIp)
                    {
                        Logger.LogWarning($"[VPNBlocker] {playerName} ({steamId}) changed IP mid-game: {originalIp} -> {currentIp}");

                        // Update to new IP
                        playerInitialIPs[steamId] = currentIp;

                        // Check the *new* IP asynchronously
                        _ = Task.Run(async () =>
                        {
                            bool isVpn = await CheckVPNStatusAsync(currentIp);

                            // Save result for audit
                            await SaveVPNIpToDatabaseAsync(currentIp, isVpn ? 1 : 0, steamId, playerName);

                            if (isVpn)
                            {
                                // Avoid duplicate notifications for this SteamID
                                if (!alreadyReportedVPN.Contains(steamId))
                                {
                                    alreadyReportedVPN.Add(steamId);

                                    Logger.LogWarning($"[VPNBlocker] Mid-game VPN detected for {playerName} ({steamId}) -> {currentIp}");

                                    Server.NextFrame(() =>
                                    {
                                        NotifyAdmins($"\x07[VPNBlocker]\x01 Player {playerName} ({steamId}) switched to a VPN mid-game: {currentIp}");
                                    });

                                    if (_c.MonitorMode == 0)
                                    {
                                        Server.NextFrame(() =>
                                        {
                                            if (player.IsValid)
                                            {
                                                player.PrintToChat(" \x07VPN/Proxy detected mid-game! Disconnecting...");
                                                Server.ExecuteCommand($"kickid {player.Slot} \"VPN/Proxy detected. Disable it to join.\"");
                                            }
                                        });
                                    }
                                }
                            }
                            // If new IP is clean → do nothing
                        });
                    }
                }
                else
                {
                    // First time seeing this player in round start, just store it
                    playerInitialIPs[steamId] = currentIp;
                }
            }

            return HookResult.Continue;
        }




        private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
        {
            var player = ev.Userid;

            // 1. Validate player
            if (player == null || !player.IsValid)
            {
                Logger.LogWarning("[VPNBlocker] Player is null or invalid, skipping VPN check.");
                return HookResult.Continue;
            }

            // 2. Extract and clean IP
            string rawIp = player.IpAddress;
            string cleanIp = rawIp?.Split(':')[0]; // remove port if present
            string playerName = player.PlayerName ?? "Unknown";
            string steamId = player.SteamID.ToString();

            Logger.LogInformation($"[VPNBlocker] Player {playerName} ({steamId}) raw IP: {rawIp}, cleaned: {cleanIp}");

            // 3. Validate cleaned IP
            if (string.IsNullOrWhiteSpace(cleanIp) || !System.Net.IPAddress.TryParse(cleanIp, out _))
            {
                Logger.LogError($"[VPNBlocker] Invalid IP after cleaning: '{cleanIp}' -> skipping VPN check");
                return HookResult.Continue;
            }

            // 4. Store initial IP for mid-game IP change detection
            playerInitialIPs[steamId] = cleanIp;

            if (IsLocalOrPrivateIP(cleanIp))
            {
                Logger.LogInformation($"[VPNBlocker] {playerName} ({steamId}) has a local/private IP ({cleanIp}), skipping VPN check.");
                return HookResult.Continue;
            }

            // 5. Whitelist check
            if (_c.WhitelistedIPs.Contains(cleanIp) || _c.WhitelistedSteamIDs.Contains(steamId))
            {
                Logger.LogInformation($"[VPNBlocker] {playerName} ({steamId}) is whitelisted. Skipping VPN check.");
                return HookResult.Continue;
            }

            // 6. Async VPN check
            _ = Task.Run(async () =>
            {
                Logger.LogInformation($"[VPNBlocker] Starting async VPN check for {playerName} ({steamId}) -> {cleanIp}");

                bool isVpn = await CheckVPNStatusAsync(cleanIp);

                // Always save result (even clean IPs) to DB with full context
                await SaveVPNIpToDatabaseAsync(cleanIp, isVpn ? 1 : 0, steamId, playerName);

                if (isVpn)
                {
                    Logger.LogWarning($"[VPNBlocker] Detected VPN for {playerName} ({steamId})");

                    // Schedule admin notification on main thread
                    Server.NextFrame(() =>
                    {
                        NotifyAdmins($"\x07[VPNBlocker] Játékos {playerName} ({steamId}) VPN-nel csatlakozik: {cleanIp}");
                    });

                    if (_c.MonitorMode == 0)
                    {
                        // Kick on main thread
                        Server.NextFrame(() =>
                        {
                            if (player.IsValid)
                            {
                                player.PrintToChat(" \x07VPN/Proxy detected! Disconnecting...");
                                Server.ExecuteCommand($"kickid {player.Slot} \"VPN/Proxy detected. Disable it to join.\"");
                            }
                        });
                    }
                    else
                    {
                        Logger.LogInformation($"[VPNBlocker] MonitorMode=1 -> Not kicking {playerName}, just logging");
                    }
                }
                else
                {
                    Logger.LogInformation($"[VPNBlocker] Player {playerName} ({steamId}) passed VPN check: {cleanIp}");
                }
            });


            return HookResult.Continue;
        }



        private async Task<bool> CheckVPNStatusAsync(string ip)
        {

            if (IsLocalOrPrivateIP(ip))
            {
                Logger.LogInformation($"[VPNBlocker] Skipping VPN check for local/private IP {ip}");
                return false;
            }

            // 1. Memory cache
            if (vpnCache.TryGetValue(ip, out bool cached))
            {
                if (cached)
                    Logger.LogWarning($"[VPNBlocker] Memory cache hit for {ip}: VPN");
                // No log for clean IPs
                return cached;
            }

            //  2. DB cache
            var dbResult = await GetVPNStatusFromDatabaseAsync(ip);
            if (dbResult.HasValue)
            {
                vpnCache[ip] = dbResult.Value; // overwrite cache from DB
                return dbResult.Value;
            }

            // 3. API call
            var (isVpn, _) = await QueryVPNApiAsync(ip);

            // Cache in memory only
            vpnCache[ip] = isVpn;
            return isVpn;
        }


        private async Task<bool?> GetVPNStatusFromDatabaseAsync(string ip)
        {
            try
            {
                await using var conn = new MySqlConnection(
                    $"Server={_c.DbHost};Database={_c.DbName};User ID={_c.DbUser};Password={_c.DbPass};"
                );
                await conn.OpenAsync();

                string query = $@"
                    SELECT block_value 
                    FROM vpn_ipaddresses 
                    WHERE ip_address=@ip 
                      AND timestamp > NOW() - INTERVAL {_c.CacheDays} DAY
                    LIMIT 1";

                await using var cmd = new MySqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@ip", ip);

                object? result = await cmd.ExecuteScalarAsync();
                if (result != null)
                {
                    int blockValue = Convert.ToInt32(result);
                    bool isVpn = blockValue > 0;
                    vpnCache[ip] = isVpn;

                    if (isVpn)
                        Logger.LogWarning($"[VPNBlocker] DB cache hit for {ip}: VPN (block={blockValue})");
                    // no log for clean hits

                    return isVpn;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[VPNBlocker] DB error: {ex.Message}");
            }

            return null; // not found in DB
        }

        private void SaveConfig()
        {
            try
            {
                string configPath = Path.Combine(ModuleDirectory, "vpnblocker_config.json");
                string json = JsonSerializer.Serialize(_c, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                Logger.LogInformation("[VPNBlocker] Config saved successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[VPNBlocker] Failed to save config: {ex.Message}");
            }
        }


        private async Task SaveVPNIpToDatabaseAsync(string ip, int blockValue, string steamId, string playerName)
        {
            try
            {
                await using var conn = new MySqlConnection(
                    $"Server={_c.DbHost};Database={_c.DbName};User ID={_c.DbUser};Password={_c.DbPass};"
                );
                await conn.OpenAsync();

                string insert = @"
                    INSERT INTO vpn_ipaddresses (ip_address, steam_id, player_name, block_value, timestamp)
                    VALUES (@ip, @steam, @name, @block, NOW())
                    ON DUPLICATE KEY UPDATE 
                        steam_id=@steam, 
                        player_name=@name,
                        block_value=@block,
                        timestamp=NOW();";

                await using var cmd = new MySqlCommand(insert, conn);
                cmd.Parameters.AddWithValue("@ip", ip);
                cmd.Parameters.AddWithValue("@steam", steamId ?? "");
                cmd.Parameters.AddWithValue("@name", playerName ?? "");
                cmd.Parameters.AddWithValue("@block", blockValue);

                await cmd.ExecuteNonQueryAsync();

                Logger.LogInformation($"[VPNBlocker] Saved IP {ip} (SteamID={steamId}, Name={playerName}) with block={blockValue} to DB.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[VPNBlocker] Failed to save VPN IP: {ex.Message}");
            }
        }

        [ConsoleCommand("css_vpn_reload_config", "Reload VPNBlocker configuration from disk")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnReloadConfig(CCSPlayerController? caller, CommandInfo command)
        {
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            try
            {
                LoadConfig();
                command.ReplyToCommand("VPNBlocker configuration reloaded successfully.");
                Logger.LogInformation("[VPNBlocker] Config reloaded via command.");
            }
            catch (Exception ex)
            {
                command.ReplyToCommand("Failed to reload config. Check server logs.");
                Logger.LogError($"[VPNBlocker] Failed to reload config: {ex.Message}");
            }
        }

        [ConsoleCommand("css_vpn_monitormode", "Set VPNBlocker MonitorMode: 1 = log only, 0 = kick mode")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnMonitorMode(CCSPlayerController? caller, CommandInfo command)
        {
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("❌ You do not have permission to use this command.");
                return;
            }

            // ✅ No argument → show current mode
            if (command.ArgCount < 2)
            {
                string modeText = _c.MonitorMode == 1
                    ? "Current MonitorMode: 1 (LOG-ONLY, no kicks)"
                    : "Current MonitorMode: 0 (KICK MODE ACTIVE)";
                command.ReplyToCommand(modeText);
                return;
            }

            string arg = command.GetArg(1);
            if (arg != "0" && arg != "1")
            {
                command.ReplyToCommand("Usage: css_vpn_monitormode <1|0>\n 1 = log only, 0 = kick mode");
                return;
            }

            int oldMode = _c.MonitorMode;
            int newMode = arg == "1" ? 1 : 0;

            _c.MonitorMode = newMode;
            SaveConfig();

            string result = newMode == 1
                ? "MonitorMode set to 1 (LOG-ONLY, no kicks)"
                : "MonitorMode set to 0 (KICK MODE ACTIVE)";

            command.ReplyToCommand(result);
            Logger.LogInformation($"[VPNBlocker] MonitorMode updated via command: {_c.MonitorMode}");

            // If switching from log-only → kick mode, enforce kicks NOW
            if (oldMode == 1 && newMode == 0)
            {
                Logger.LogInformation("[VPNBlocker] Enforcing kicks for all VPN users (rechecking all players)...");

                foreach (var player in Utilities.GetPlayers())
                {
                    if (player == null || !player.IsValid) continue;

                    string steamId = player.SteamID.ToString();
                    string ip = player.IpAddress?.Split(':')[0];
                    string playerName = player.PlayerName ?? "Unknown";

                    // Skip whitelisted
                    if (_c.WhitelistedIPs.Contains(ip) || _c.WhitelistedSteamIDs.Contains(steamId))
                        continue;

                    // Skip LAN/loopback
                    if (IsLocalOrPrivateIP(ip))
                        continue;

                    // If already flagged as VPN → kick immediately
                    if (alreadyReportedVPN.Contains(steamId))
                    {
                        Server.NextFrame(() =>
                        {
                            if (player.IsValid)
                            {
                                Logger.LogWarning($"[VPNBlocker] Kicking {playerName} ({steamId}) for previously detected VPN.");
                                player.PrintToChat(" \x07VPN/Proxy detected! Disconnecting...");
                                Server.ExecuteCommand($"kickid {player.Slot} \"VPN/Proxy detected. Disable it to join.\"");
                            }
                        });
                        continue;
                    }

                    // Otherwise re-check their current IP asynchronously
                    _ = Task.Run(async () =>
                    {
                        bool isVpn = await CheckVPNStatusAsync(ip);

                        // Save re-check result
                        await SaveVPNIpToDatabaseAsync(ip, isVpn ? 1 : 0, steamId, playerName);

                        if (isVpn)
                        {
                            alreadyReportedVPN.Add(steamId);
                            Server.NextFrame(() =>
                            {
                                if (player.IsValid)
                                {
                                    Logger.LogWarning($"[VPNBlocker] Mid-game enforcement: kicking {playerName} ({steamId}) for VPN {ip}");
                                    player.PrintToChat(" \x07VPN/Proxy detected! Disconnecting...");
                                    Server.ExecuteCommand($"kickid {player.Slot} \"VPN/Proxy detected. Disable it to join.\"");
                                }
                            });
                        }
                    });
                }
            }
        }




        [ConsoleCommand("css_vpn_whitelist_id", "Whitelist a SteamID from VPN checks")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnWhitelist(CCSPlayerController? caller, CommandInfo command)
        {
            // Permission check: only admins can run
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            if (command.ArgCount < 2)
            {
                command.ReplyToCommand("Usage: css_vpn_whitelist_id <SteamID64>");
                return;
            }

            string steamId = command.GetArg(1);

            // Basic validation
            if (string.IsNullOrWhiteSpace(steamId) || steamId.Length < 10)
            {
                command.ReplyToCommand($"Invalid SteamID: {steamId}");
                return;
            }

            // Already whitelisted?
            if (_c.WhitelistedSteamIDs.Contains(steamId))
            {
                command.ReplyToCommand($"SteamID already whitelisted: {steamId}");
                return;
            }

            // Add to whitelist
            _c.WhitelistedSteamIDs.Add(steamId);
            SaveConfig();

            command.ReplyToCommand($"SteamID {steamId} has been added to the VPN whitelist.");
        }

        [ConsoleCommand("css_vpn_whitelist_ip", "Whitelist an IP address from VPN checks")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnWhitelistIP(CCSPlayerController? caller, CommandInfo command)
        {
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            if (command.ArgCount < 2)
            {
                command.ReplyToCommand("Usage: css_vpn_whitelist_ip <IPv4>");
                return;
            }

            string ip = command.GetArg(1);

            // Basic IPv4 validation
            if (!System.Net.IPAddress.TryParse(ip, out var parsedIp) || parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                command.ReplyToCommand($"Invalid IPv4 address: {ip}");
                return;
            }

            if (_c.WhitelistedIPs.Contains(ip))
            {
                command.ReplyToCommand($"IP already whitelisted: {ip}");
                return;
            }

            _c.WhitelistedIPs.Add(ip);
            SaveConfig();

            command.ReplyToCommand($"IP {ip} has been added to the VPN whitelist.");
        }

        [ConsoleCommand("css_vpn_unwhitelist_id", "Remove a SteamID from VPN whitelist")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnUnwhitelistID(CCSPlayerController? caller, CommandInfo command)
        {
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            if (command.ArgCount < 2)
            {
                command.ReplyToCommand("Usage: css_vpn_unwhitelist_id <SteamID64>");
                return;
            }

            string steamId = command.GetArg(1);

            if (!_c.WhitelistedSteamIDs.Contains(steamId))
            {
                command.ReplyToCommand($"SteamID {steamId} is not currently whitelisted.");
                return;
            }

            _c.WhitelistedSteamIDs.Remove(steamId);
            SaveConfig();

            command.ReplyToCommand($"SteamID {steamId} has been removed from the VPN whitelist.");
        }

        [ConsoleCommand("css_vpn_unwhitelist_ip", "Remove an IP from VPN whitelist")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void CommandVpnUnwhitelistIP(CCSPlayerController? caller, CommandInfo command)
        {
            if (caller != null && !IsAdmin(caller))
            {
                command.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            if (command.ArgCount < 2)
            {
                command.ReplyToCommand("Usage: css_vpn_unwhitelist_ip <IPv4>");
                return;
            }

            string ip = command.GetArg(1);

            if (!_c.WhitelistedIPs.Contains(ip))
            {
                command.ReplyToCommand($"IP {ip} is not currently whitelisted.");
                return;
            }

            _c.WhitelistedIPs.Remove(ip);
            SaveConfig();

            command.ReplyToCommand($"IP {ip} has been removed from the VPN whitelist.");
        }


        private bool IsLocalOrPrivateIP(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var addr))
                return false;

            // Loopback (127.0.0.1, ::1)
            if (IPAddress.IsLoopback(addr))
                return true;

            // IPv4 private ranges
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] bytes = addr.GetAddressBytes();
                switch (bytes[0])
                {
                    case 10: // 10.0.0.0/8
                        return true;
                    case 172: // 172.16.0.0 - 172.31.255.255
                        return bytes[1] >= 16 && bytes[1] <= 31;
                    case 192: // 192.168.0.0/16
                        return bytes[1] == 168;
                }
            }

            // IPv6 unique local addresses (fc00::/7)
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                byte[] bytes = addr.GetAddressBytes();
                return (bytes[0] & 0xFE) == 0xFC; // fc00::/7
            }

            return false;
        }


        private async Task<(bool isVpn, int blockValue)> QueryVPNApiAsync(string ip)
        {
            try
            {
                // Reject IPv6 early
                if (IPAddress.TryParse(ip, out var parsedIp) && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    Logger.LogWarning($"[VPNBlocker] Skipping IPv6 address {ip}, IPHub only supports IPv4.");
                    return (false, 0);
                }

                // Validate API key
                if (string.IsNullOrWhiteSpace(_c.IpHubApiKey) || _c.IpHubApiKey == "YOUR_API_KEY_HERE")
                {
                    Logger.LogError("[VPNBlocker] Missing or invalid IPHub API key! Please set IpHubApiKey in vpnblocker_config.json");
                    return (false, 0);
                }

                // Ensure HTTPS endpoint
                string apiBase = _c.VpnApiUrl.StartsWith("http") ? _c.VpnApiUrl : "https://v2.api.iphub.info/ip/{0}";
                string url = string.Format(apiBase, ip);

                Logger.LogInformation($"[VPNBlocker] Querying API: {url}");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Key", _c.IpHubApiKey);

                using var response = await httpClient.SendAsync(request);

                Logger.LogInformation($"[VPNBlocker] API HTTP Status: {(int)response.StatusCode} {response.StatusCode}");

                string body = await response.Content.ReadAsStringAsync();

                // Handle common errors
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    Logger.LogError($"[VPNBlocker] API returned 422 Unprocessable Entity for {ip}. Body: {body}");
                    return (false, 0);
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Logger.LogError($"[VPNBlocker] Invalid API key or exceeded quota. Body: {body}");
                    return (false, 0);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError($"[VPNBlocker] API failed with {response.StatusCode}: {body}");
                    return (false, 0);
                }

                // Parse JSON
                using JsonDocument doc = JsonDocument.Parse(body);

                int blockValue = 0;
                if (doc.RootElement.TryGetProperty("block", out var blockElem))
                {
                    blockValue = blockElem.GetInt32();
                }
                else
                {
                    Logger.LogWarning($"[VPNBlocker] No 'block' field in API response. Body: {body}");
                }

                Logger.LogInformation($"[VPNBlocker] API returned block={blockValue} for {ip}");

                return (blockValue > 0, blockValue);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[VPNBlocker] API error: {ex.Message}");
                return (false, 0);
            }
        }

    }
}
