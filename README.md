# ✅ SaySounds4 for CounterStrikeSharp

A **CounterStrikeSharp plugin** for CS2 that lets players trigger fun sound effects via chat commands. It’s lightweight, configurable, and supports per-player mute preferences saved to a MySQL database.

This plugin is ideal for community servers that want simple **chat-triggered sounds**.

> **Disclaimer:**  
> This plugin does **not** provide sound downloads; you must ensure clients have the correct sound files installed.

---

## 📦 Requirements

- **CounterStrikeSharp** installed on your CS2 server
- **MultiAddon manager** installed on your CS2 server  
- **.NET 8+ runtime** (required by CSS plugins)  
- **MySQL/MariaDB database** 
- Sound files placed in `csgo/sounds/saysounds/` inside your custom workshop addon. (Take a look at this with Source2Viewer: https://steamcommunity.com/sharedfiles/filedetails/?id=3526275068)

---

## ✨ Features

✅ **Chat-triggered sounds**  
- Players type keywords like `apam` or `boom` in chat to play sounds.

✅ **Mute toggle**  
- `!toggless` lets players mute or unmute SaySounds for themselves.

✅ **Trigger list**  
- `!saysounds` prints the full list of available triggers.

✅ **Cooldown control**  
- Prevents spam with configurable cooldown per player.

✅ **Admin-only mode**  
- Optional **AdminOnly** restriction so only admins can trigger sounds.

✅ **Configurable admin group**  
- Default `@css/generic` but can be changed to `@css/root` or others in the config.

✅ **Persistent mute states**  
- Stores mute preferences in a MySQL database with auto-created SQL table.

---

## ❌ What It Is NOT

🚫 **Not a precacher/downloader** – You must manage sound files yourself in your custom workshop addon.
🚫 **Not a sound spam plugin** – It’s controlled & cooldown-limited.  
🚫 **Not a GUI-based system** – No advanced menus, only chat commands.

---

## 🗄️ Database Setup

Run this SQL script to create the table for saving mute states:

```sql
CREATE TABLE IF NOT EXISTS saysounds_preferences (
    steamid VARCHAR(32) NOT NULL PRIMARY KEY,
    muted TINYINT(1) NOT NULL DEFAULT 0,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

---

## ⚙️ Installation

1. **Copy the plugin**  
   Place the latest release into `csgo/addons/counterstrikesharp/plugins/` following the existing folder structure.

2. **Load the plugin once**  
   It will auto-generate `saysounds_config.json`.

3. **Edit the config**  
   - Add DB credentials
   - Configure triggers & cooldown
   - Set `AdminOnly` or change `AdminGroup` if needed

4. **Upload sound files**  
   Place all required `.wav` or `.mp3` files in `csgo/sounds/saysounds/` inside your custom workshop addon.

5. **Restart the plugin or the server**  
   Enjoy SaySounds!

---

## 💬 Default Commands

| Command | Description |
|---------|-------------|
| `!saysounds` | Shows the available list of triggers |
| `!toggless` | Toggles SaySounds on/off for the player |
| *chat trigger* | Typing a trigger keyword plays the sound |

---

## 🛠️ Configuration

`saysounds_config.json` example:

```json
{
  "Database": {
    "Host": "localhost",
    "Port": 3306,
    "Name": "dbname",
    "User": "dbuser",
    "Password": "userpass"
  },
  "SoundCooldownSeconds": 10,
  "AdminOnly": false,
  "AdminGroup": "@css/generic",
  "Triggers": {
    "apam": "sounds/saysounds/apam.wav",
    "boom": "sounds/saysounds/boom.wav"
  }
}
```

---

✅ **Simple, lightweight, configurable SaySounds plugin for community servers!**
