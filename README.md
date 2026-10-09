# Traffic Light

Shows the state of every Claude Code session open on the machine, so you know which one needs you:

- 🟡 **NEEDS INPUT / AGUARDANDO**: asked for permission or asked a question
- 🔴 **WORKING / ATIVO**: working
- 🟢 **IDLE / OCIOSO**: finished, waiting for the next prompt

Sessions waiting on you come first, one line per session (folder, state and time of the last change).

## Windows app (`traffic_light.exe`)

Download `traffic_light.exe` from the [latest release](https://github.com/PauloAbrahao/Traffic-Light/releases/latest) and double-click it. You can copy just the `.exe` to any folder or Windows machine (it uses .NET Framework 4, which ships with Windows 10/11).

- **Hook installation:** on startup it checks `~/.claude/settings.json`. If any hook is missing, it shows what's missing and asks to install it (without removing your hooks; it backs up to `settings.json.bak`). Afterwards, restart any open Claude Code sessions.
- **Tray icon:** color of whoever needs you most (yellow > red > green; gray = no sessions). Hover to see the summary.
- **Widget:** always on top. Starts in the bottom-right corner; drag to move and it remembers the spot. `···` copies the path or removes the row, `–` collapses it to a small bar with one colored dot per session (rest the mouse on a dot for 1.5s to see that session; `⌄` expands it again), the sliders icon opens the menu (also where you switch between English and Português; the choice is saved and overrides `--ptbr`).
- **Menu** (right-click the icon or the widget): show/hide widget, **verify installation**, **uninstall**, language, exit.
- **Uninstall:** removes only the traffic light hooks from `~/.claude/settings.json` (your other hooks stay; backup in `settings.json.bak`), clears its saved settings (position, language, collapsed) and closes. Then delete `traffic_light.exe`. A single click on the icon shows/hides the widget.

English by default; `traffic_light.exe --ptbr` for pt-BR. To open in pt-BR on double-click, create a shortcut and add `--ptbr` to the end of the "Target" field.

If another traffic light is already running, it warns that the port is in use and doesn't open.

## Hooks (reference)

The exe already does this. The traffic light receives events via HTTP hooks at `http://127.0.0.1:4545`; in `~/.claude/settings.json`, each of these events needs a hook pointing to that URL:

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
<img width="1920" height="1080" alt="Animação" src="https://github.com/user-attachments/assets/2b5b4196-d042-485e-bb0f-4eedcd5da78a" /> </br>
<img width="386" height="171" alt="idle" src="https://github.com/user-attachments/assets/122f70d1-b783-4efc-b65b-9b1fca36e4f4" /></br>
<img width="561" height="228" alt="configs" src="https://github.com/user-attachments/assets/59ffc831-4a34-4f70-9b9d-7cab66d1acc2" /></br>


## Releasing

Push a version tag and GitHub Actions builds `traffic_light.exe` on Windows and attaches it to a new release:

```
git tag v1.0.0
git push origin v1.0.0
```
