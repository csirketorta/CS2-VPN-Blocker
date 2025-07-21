# ✅ VPNBlocker for CounterStrikeSharp  

A **CounterStrikeSharp plugin** that detects and optionally blocks players connecting via VPNs or proxies in CS2 servers.  

This plugin integrates with **[IPHub’s VPN detection API](https://iphub.info/)** because it’s simple to set up, lightweight, and reliable.  

- ✅ **Free plan:** Provides **1,000 queries per day**, which is enough for most small or medium community servers.  
- ✅ **Paid plans:** Available for larger servers with higher player counts.

> **Disclaimer:**  
> This project is **not sponsored, endorsed, or officially supported by IPHub**.  
> It simply uses their public API as a VPN detection source.  

VPNBlocker supports:  
- **SteamID/IP whitelisting**  
- **Mid-game IP change detection**  
- A flexible **MonitorMode** that can either **log detections for admin review** or **actively kick VPN users**.  
---

## 📦 Requirements  

- **CounterStrikeSharp** installed on your CS2 server  
- **.NET 8+ runtime** (required by CSS plugins)  
- A working **MySQL database** for caching results  
- A valid **IPHub API key** for VPN/proxy detection  
- Internet connectivity for API calls  

---

## ✨ Features  

✅ **VPN & Proxy Detection**  
- Detects VPN/proxy users on connect using **IPHub API**  
- Caches results in MySQL + memory for fast lookups  
- Skips checks for **LAN/private/loopback IPs**  

✅ **Whitelist Support**  
- Whitelist **specific SteamIDs** or **IP addresses**  
- Manage whitelist dynamically via admin commands  

✅ **Monitor Modes**  
- `MonitorMode=1` → **Log-only mode** (alerts admins but doesn’t kick)  
- `MonitorMode=0` → **Kick mode** (auto-disconnects VPN users)  
- Change mode live without restart  

✅ **Mid-Game Detection**  
- Detects **IP changes mid-match**  
- If a player switches to a VPN mid-game → alerts admins & optionally kicks  
- When switching to kick mode mid-game → re-checks all players & enforces kicks  

✅ **Admin Commands**  

| Command | Arguments | Description |
|---------|-----------|-------------|
| **css_vpn_reload_config** | *(none)* | Reloads `vpnblocker_config.json` without restarting the server |
| **css_vpn_monitormode** | *(none)* | Shows current MonitorMode (1 = log-only, 0 = kick mode) |
| **css_vpn_monitormode** | `1` or `0` | Sets MonitorMode: `1` = log-only, `0` = kick mode (re-checks & enforces kicks immediately) |
| **css_vpn_whitelist_id** | `<SteamID64>` | Adds a SteamID to the whitelist (skips VPN checks) |
| **css_vpn_unwhitelist_id** | `<SteamID64>` | Removes a SteamID from the whitelist |
| **css_vpn_whitelist_ip** | `<IPv4>` | Adds an IP address to the whitelist |
| **css_vpn_unwhitelist_ip** | `<IPv4>` | Removes an IP address from the whitelist |


✅ **Performance-Friendly**  
- Async HTTP requests (no server lag)  
- Database + memory cache reduces API calls  

---

## ❌ What It Is NOT  

🚫 **Not a global VPN database** – Uses an external API (IPHub).  
🚫 **Not a permanent ban system** – Only logs or kicks temporarily.  
🚫 **Not a GeoIP blocker** – Does not restrict by country/region.  
🚫 **Not a cheat detection plugin** – Only detects VPN/proxy connections.  

---

## ⚙️ Installation  

1. **Get an API Key**  
   - Register on [IPHub](https://iphub.info) and get a free/paid API key.  

2. **Prepare a MySQL Database**  
   - Create a MySQL database for caching IP checks.  
   - Create the required table with the following script:  

```sql
CREATE TABLE IF NOT EXISTS `vpn_ipaddresses` (
  `ip_address` VARCHAR(45) NOT NULL,
  `steam_id` VARCHAR(64) DEFAULT NULL,
  `player_name` VARCHAR(64) DEFAULT NULL,
  `block_value` TINYINT(1) NOT NULL DEFAULT 0,
  `timestamp` TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`ip_address`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

3. **Install CounterStrikeSharp**  
   - Follow [CSS installation guide](https://docs.cssharp.dev).  

4. **Upload the Plugin**  
   - Download and place the latest release into your `addons/counterstrikesharp/plugins/` folder.

5. **Load the plugin once**  
   - It will create a default config file `vpnblocker_config.json` in the plugin folder.  

6. **Edit Config**  
   - Add your API key & database credentials. Example:  
   ```json
   {
     "MonitorMode": 1,
     "DbHost": "127.0.0.1",
     "DbUser": "cs2user",
     "DbPass": "mypassword",
     "DbName": "vpnblocker",
     "VpnApiUrl": "http://v2.api.iphub.info/ip/{0}",
     "IpHubApiKey": "YOUR_API_KEY_HERE",
     "CacheDays": 180,
     "WhitelistedIPs": [
       "123.45.67.89",
       "98.76.54.32"
     ],
     "WhitelistedSteamIDs": [
       "76561198076843812"
     ]
   }
7. **Reload the plugin after editing the config**
   - css_plugins reload "VPN Blocker"
