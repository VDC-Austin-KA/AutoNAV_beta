# AutoNAV MCP — plugin install

Prebuilt plugin DLLs for Navisworks Manage 2024–2027 plus an installer. This is what makes the **AutoNAV MCP** button appear on the Navisworks **Add-Ins** ribbon tab.

```
dist/
  2024/AutoNAVMCP.dll   ← for Navisworks Manage 2024
  2025/AutoNAVMCP.dll   ← 2025
  2026/AutoNAVMCP.dll   ← 2026
  2027/AutoNAVMCP.dll   ← 2027
  Install-AutoNAVMCP.ps1
  Uninstall-AutoNAVMCP.ps1
```

## Install (recommended)

1. Right-click **Start → Windows PowerShell (Admin)** (must be elevated — it writes under `C:\Program Files`).
2. Run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-AutoNAVMCP.ps1
   ```
3. It detects every installed Navisworks Manage 2024–2027, copies the **matching year's** DLL to `…\Navisworks Manage <year>\Plugins\AutoNAVMCP\AutoNAVMCP.dll`, and **unblocks** it.
4. Restart Navisworks → **Add-Ins** tab → **AutoNAV MCP**. The button toggles the bridge on `127.0.0.1:5711`.

## Manual install (if you prefer)

For your Navisworks year, create the folder and copy the matching DLL:

```
C:\Program Files\Autodesk\Navisworks Manage 2025\Plugins\AutoNAVMCP\AutoNAVMCP.dll
```

Then **unblock it** — right-click the DLL → **Properties** → check **Unblock** → OK (or `Unblock-File` in PowerShell). Restart Navisworks.

Two rules that make or break it:
- The folder **must** be named `AutoNAVMCP` (exactly the DLL name) — Navisworks only loads a plugin from a subfolder named after its assembly.
- Use the DLL from the folder that **matches your Navisworks year**. A 2026 DLL will not load in 2025, and vice-versa.

## "The button still isn't there" — checklist

| Check | Fix |
|---|---|
| **DLL blocked?** (most common) | Right-click the installed `AutoNAVMCP.dll` → Properties → **Unblock**. The installer does this automatically; a manual copy does not. |
| **Right year?** | The DLL must come from the `dist\<year>\` folder matching your Navisworks Manage version. Mixed-up years fail silently. |
| **Right folder name?** | Must be `…\Plugins\AutoNAVMCP\AutoNAVMCP.dll` — not `Plugins\AutoNAVMCP.dll` and not a differently-named folder. |
| **Looking in the right place?** | It's under the **Add-Ins** ribbon tab (as a Tool Add-in), not a tab of its own. |
| **Navisworks Manage (not Freedom/Simulate)?** | Clash Detective and .NET plugins require Navisworks **Manage**. |
| **Fully restarted Navisworks** after installing? | Plugins load at startup only. |

If it's still missing after all of the above, tell me your exact Navisworks Manage version and I'll dig further.
