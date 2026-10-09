# Traffic Light

Shows the state of every Claude Code session open on the machine, so you know which one needs you:

- 🟡 **WAITING FOR YOU / AGUARDANDO VOCE**: asked for permission or asked a question
- 🔴 **BUSY / ATIVO**: working
- 🟢 **IDLE / LIVRE**: finished, waiting for the next prompt

In the Windows app these show as **NEEDS INPUT / AGUARDANDO**, **WORKING / ATIVO** and **IDLE / OCIOSO**.

Sessions waiting on you come first, one line per session (folder, state and time of the last change).

There are two versions with the same logic. Use **one or the other** (both listen on port 4545):

| Version | File | Requires |
|---|---|---|
| **Windows app** (recommended) | `traffic_light.exe` | nothing: it's self-contained |
| **Node** | `traffic_light.js` | Node.js (Edge only for `--app`) |

What both do the same:

| | exe | Node |
|---|---|---|
| States, ordering and events | ✓ | ✓ |
| Folder + state + time per session | widget | terminal / window |
| Summary (`1 waiting · 2 busy · 0 idle`) | tray icon tooltip | top of the panel |
| Detects Esc via the transcript | within 1s | immediately |
| Checks hooks on startup | asks to install | warns below the panel |
| Install hooks | on startup / menu | `--install` |
| Verify installation | menu | `--doctor` |
| Port 4545 in use | warns and doesn't open | warns and exits |
| `--ptbr` | ✓ | ✓ |

## Windows app (`traffic_light.exe`)

Double-click it. You can copy just the `.exe` to any folder or Windows machine (it uses .NET Framework 4, which ships with Windows 10/11).

- **Hook installation:** on startup it checks `~/.claude/settings.json`. If any hook is missing, it shows what's missing and asks to install it (without removing your hooks; it backs up to `settings.json.bak`). Afterwards, restart any open Claude Code sessions.
- **Tray icon:** color of whoever needs you most (yellow > red > green; gray = no sessions). Hover to see the summary.
- **Widget:** always on top. Starts in the bottom-right corner; drag to move and it remembers the spot. `···` copies the path or removes the row, `–` collapses it to a small bar with one colored dot per session (hover for the list, `⌄` expands it again), the sliders icon opens the menu (also where you switch between English and Português; the choice is saved and overrides `--ptbr`).
- **Menu** (right-click the icon or the widget): show/hide widget, **verify installation**, **uninstall**, language, exit.
- **Uninstall:** removes only the traffic light hooks from `~/.claude/settings.json` (your other hooks stay; backup in `settings.json.bak`), clears its saved settings (position, language, collapsed) and closes. Then delete `traffic_light.exe`. A single click on the icon shows/hides the widget.

English by default; `traffic_light.exe --ptbr` for pt-BR. To open in pt-BR on double-click, create a shortcut and add `--ptbr` to the end of the "Target" field.

If another traffic light is already running (the exe or the `.js`), it warns that the port is in use and doesn't open.

## Node version (`traffic_light.js`)

| Command | What it does |
|---|---|
| `node traffic_light.js` | Panel in the terminal. If a hook is missing, shows a warning below. |
| `node traffic_light.js --app` | Panel in a window (Edge in app mode). Closing the window exits. If a `.js` traffic light is already running, just opens another window for it. |
| `node traffic_light.js --install` | Installs the hooks (with backup) and verifies |
| `node traffic_light.js --doctor` | Checks `settings.json`, each hook and matcher, `disableAllHooks`, Edge (only for `--app`) and whether the traffic light (exe or `.js`) is running. Exits with code 1 if it finds a problem. |

`--ptbr` works with all of them. Flags can come in any order.

## Hooks (reference)

The exe and `--install` already do this. The traffic light receives events via HTTP hooks at `http://127.0.0.1:4545`; in `~/.claude/settings.json`, each of these events needs a hook pointing to that URL:

`SessionStart`, `UserPromptSubmit`, `PostToolUse`, `PreToolUse` (matcher `AskUserQuestion`), `PermissionRequest`, `Notification` (matcher `permission_prompt|elicitation_dialog|idle_prompt`), `Stop`, `SessionEnd`

```json
"Stop": [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:4545", "timeout": 2 }] }]
```

With the traffic light closed, the hooks just fail silently. It doesn't get in Claude Code's way.

## Notes

- State is kept in memory only: open the traffic light before your sessions, or wait for each session's next event for it to show up.
- Esc (interrupt) doesn't fire any hook. The traffic light detects the interruption by reading the session transcript (in the exe, within 1s).

## Rebuilding the exe

Only needed if you change `traffic_light.cs`. Close the exe first (menu → Exit):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:traffic_light.exe traffic_light.cs
```

## Screenshots

**Windows widget**
<img width="2549" height="1243" alt="Animação" src="https://github.com/user-attachments/assets/54875e05-14c3-4f2c-9233-a6cef069f76f" />
----
<img width="1115" height="630" alt="busy" src="https://github.com/user-attachments/assets/7dec826e-6c0b-4144-9e62-d2b0e740eaee" />  </br>
<img width="1117" height="629" alt="idle" src="https://github.com/user-attachments/assets/6c068328-5fcc-4382-99f3-e043c6e3c1ab" /> </br>
<img width="1112" height="631" alt="waiting" src="https://github.com/user-attachments/assets/c8585483-b9aa-40d1-a23c-af90b365ce33" /> </br>

