# Publishing the Navisworks agent to Microsoft Teams (company-wide)

This guide takes the Copilot Studio agent you already built (the one wired to the AutoNAV MCP server per [COPILOT.md](COPILOT.md)) and makes it a **Teams app your whole company can add**, the same way your other internally-built agents appear in Teams.

If the agent works when you test it inside Copilot Studio but **doesn't show up in Teams for anyone (including you)**, you've simply not completed the *publish-to-channel* and *admin-approval* steps. That's what most of this guide is about.

---

## ⚠️ Read this first — the hosting reality

A Teams app built this way is just a **chat front-end** to your Copilot Studio agent. When someone chats with it, the agent (in Microsoft's cloud) calls your **MCP HTTP endpoint**, which talks to the **AutoNAV bridge inside one running Navisworks session**.

That has three consequences for a company-wide rollout:

1. **The MCP endpoint must be permanently reachable from Microsoft's cloud.** A `devtunnel` you start by hand (from COPILOT.md) is fine for *you* testing, but it dies when you close the window and its URL changes each run. For an org deployment you need a **stable, always-on URL** (see "Stable hosting" below).
2. **Every user drives the same Navisworks session.** There is one open model on one machine. The Teams app lets many people *trigger* coordination actions, but they're all acting on that single session — this is a shared "coordination console," not a per-user tool. Decide who is allowed to run write actions (assign, group, generate) vs. just read/report.
3. **When Navisworks is closed, the tools return "Lost connection to the Navisworks bridge."** That's expected — the coordination PC has to be running Navisworks with the AutoNAV MCP bridge started.

If that model doesn't fit what you want, tell me — there are other designs (e.g. a queue, or a headless Navisworks automation service), but they're a larger build.

---

## Stable hosting for the MCP endpoint (do this before publishing org-wide)

Pick one. All of them replace the ad-hoc `devtunnel host` from COPILOT.md with something that stays up.

| Option | Good for | How |
|---|---|---|
| **Persistent dev tunnel** | Quickest upgrade from testing | `devtunnel create autonav --allow-anonymous` then `devtunnel port create autonav -p 3711` then `devtunnel host autonav`. The URL stays constant across restarts. Run it as a service/scheduled task on the Navisworks PC so it survives reboots. |
| **Azure App Service / VM + reverse proxy** | Proper IT-owned deployment | Host the Node MCP server (or a reverse proxy to the Navisworks PC) behind a fixed `https://…azurewebsites.net/mcp`. Lock it down with the auth option below. |
| **Cloudflare Tunnel / named ngrok domain** | Fixed URL without Azure | Reserved subdomain that always maps to port 3711 on the Navisworks PC. |

Whatever you choose, the agent's tool **Server URL** in Copilot Studio must point at `https://<your-stable-host>/mcp`, and that host must stay running whenever people use the Teams app.

> Security: prefer an **authenticated** endpoint over `--allow-anonymous`. The AutoNAV bridge itself only listens on `127.0.0.1` (never the network); only the MCP HTTP layer is exposed, and only your hosting choice reaches it. Coordinate with IT — this endpoint can edit a live model.

---

## Part 1 — Publish the agent (makes your latest changes live)

1. Open the agent at **https://copilotstudio.microsoft.com**.
2. Confirm the **Navisworks MCP tool's Server URL** points at your *stable* host from above (Tools → your MCP tool → edit the URL if it still points at an old tunnel).
3. Top-right → **Publish** → confirm. Wait for "Publishing succeeded."

Re-publish every time you change the agent, its tools, or the endpoint URL.

---

## Part 2 — Add the Teams + Microsoft 365 Copilot channel

1. In the agent, left nav → **Channels**.
2. Click **Teams + Microsoft 365 Copilot**.
3. Click **Add channel** (then **Turn on Teams** if prompted).
4. Click **Publish** / **See agent in Teams** to generate the Teams app package.

At this point the agent *can* run in Teams, but it's only available to **you** until Part 3.

---

## Part 3 — Make it available to the whole company (the missing step)

This is almost certainly what you're missing. On the **Teams + Microsoft 365 Copilot** channel page there's an **Availability options** button. Inside it you have three ways out:

### Option A — Submit for org-wide admin approval (recommended for "everyone")
1. **Availability options → Show to everyone in my org → Submit for admin approval.**
2. This sends the app to **Teams Admin Center**. A **Teams administrator** must approve it:
   - Admin goes to **admin.teams.microsoft.com → Teams apps → Manage apps**.
   - Find the app (status *Submitted / Pending approval*) → open it → **Publish** / **Allow**.
   - (Optional) Under **Setup policies**, pin it so it auto-appears for chosen users/groups.
3. Once approved it shows up in Teams under **Apps → Built for your org**, and anyone in scope can **Add** it — exactly like your other internal agents.

> If **you are not a Teams admin**, this submit step is as far as you can go; someone with the Teams Administrator role has to do the approval. Send them the app name and this section.

### Option B — Share with specific people or a security group (no admin needed)
- On the channel page use **Share** → add individuals or an Entra **security group** (e.g. "VDC Team"). They'll get a link to add it. Good for a pilot before going org-wide.

### Option C — Download the app package and hand it to IT
- **Availability options → Download the app (.zip)**. This `.zip` is a standard Teams app package (manifest + icons).
- IT uploads it in **Teams Admin Center → Manage apps → Upload new app**, or you sideload it via **Teams → Apps → Manage your apps → Upload an app** (if your tenant allows custom-app uploads).

**Why only you can see it today:** the agent has only been published to the channel and shared with its owner (you). Until Option A (admin approval) or Option B (explicit share) is done, no one else — and no org app catalog — knows it exists.

---

## Part 4 — Users add it in Teams

Once approved org-wide, tell your team:
1. Teams → **Apps** (left rail) → **Built for your org** (or search the app name).
2. **Add**. It opens as a chat you message like any other agent.
3. Try: *"Check Navisworks status,"* then *"Run all clash tests and give me an HTML report of Active clashes."*

---

## Authentication note

For a company-wide Teams app, set the agent's **Authentication** (Copilot Studio → **Settings → Security → Authentication**) to **Authenticate with Microsoft** (Entra ID), not "No authentication." That ties access to your tenant's sign-in and is usually required before an admin will approve org-wide publishing. (This authenticates *users to the agent*; it's separate from how the agent's MCP tool authenticates to your endpoint.)

## Model note (Opus 4.8)

Your choice of Opus 4.8 as the agent's model is set in Copilot Studio (agent **Settings → Generative AI / model**) and rides along automatically — nothing extra is needed for Teams publishing. The Teams app is just a front-end; it uses whatever model the published agent is configured with.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| App works in Copilot Studio, not visible in Teams | Part 2 not done, or Part 3 approval pending. Check **Manage apps** status in Teams Admin Center. |
| Only you can see/add it | Part 3 not done — submit for admin approval (A) or share with a group (B). |
| Admin can't find it to approve | Re-open the channel and click **Submit for admin approval** again; it must reach *Manage apps* with a *Pending* status. |
| Tools error "Lost connection to the Navisworks bridge" | Navisworks isn't open with the AutoNAV MCP bridge started, or your hosted endpoint is down / URL changed. |
| Worked yesterday, dead today | You were on an ad-hoc `devtunnel`; move to stable hosting (see above) and update the tool's Server URL, then **re-publish**. |
| "Model Context Protocol" tool option missing when editing | Tenant admin must enable MCP/custom tools for Copilot Studio in your environment. |

---

Need help choosing and standing up the stable hosting (Part 0), or writing the exact request to send your Teams admin? Tell me whether you're a Teams administrator and how the endpoint is hosted today, and I'll tailor the next steps.
