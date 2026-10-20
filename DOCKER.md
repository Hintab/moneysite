> **[DRAFT — PENDING REVIEW]**

# Running MoneySite in Docker on Unraid

The image holds only the app. All your data (Plaid connections, transactions, categories,
keyword rules, merchant groupings, account labels, balance history) lives in one SQLite
database, `money.db`, in the folder mapped to `/data`. On Unraid that's
`/mnt/user/appdata/moneysite`.

A fresh install needs no manual setup. On every start the container creates `/data` if
needed, hands it to `PUID:PGID` (default `99:100`, Unraid's `nobody:users`), then starts
the app as that user, never as root. An empty folder becomes a new, empty database.

## ⚠ Security warning: the app has no login

Anyone who can reach the app's address can see every transaction and balance, and can
**disconnect your banks**. There is no password screen.

- Keep it reachable only from your home network. For access away from home, use a VPN
  (for example Tailscale), or a reverse proxy that requires a login.
- **Never port-forward it on your router** or expose it through a public tunnel.

## 1. Get the template onto Unraid

Unraid 7 no longer has a "Template repositories" setting. Instead, a small script pulls
every template from [Hintab/docker_templates](https://github.com/Hintab/docker_templates)
into Unraid's **default templates** folder.

Use `/boot/config/plugins/dockerMan/templates/…`, **not** `templates-user`. Unraid finds a
container's saved settings by taking the first file in `templates-user` whose `<Name>`
matches the container. A plain copy of the template there gets picked instead of your
saved `my-MoneySite.xml`, so **Edit** shows the defaults, and **Apply update** rebuilds the
container without your Plaid keys.

1. Install the **User Scripts** plugin from the Apps tab.
2. In the Unraid terminal, create the script and run it once:

   ```bash
   mkdir -p /boot/config/plugins/user.scripts/scripts/docker-templates
   cat > /boot/config/plugins/user.scripts/scripts/docker-templates/script <<'EOF'
   #!/bin/bash
   #description=Pulls every template from Hintab/docker_templates into Unraid's default templates
   DEST=/boot/config/plugins/dockerMan/templates/hintab
   TMP=$(mktemp -d)
   if curl -fsSL https://github.com/Hintab/docker_templates/archive/refs/heads/main.tar.gz | tar -xz -C "$TMP"; then
     mkdir -p "$DEST" && rm -f "$DEST"/*.xml
     cp "$TMP"/docker_templates-main/*.xml "$DEST/" && ls "$DEST"
   else
     echo "couldn't download templates from GitHub"
   fi
   rm -rf "$TMP"
   EOF
   bash /boot/config/plugins/user.scripts/scripts/docker-templates/script
   ```

3. Under **Settings → User Scripts**, set **docker-templates** to **Scheduled Daily** and
   click **Apply**, so template changes on GitHub show up by themselves.

## 2. Add the container

1. **Docker → Add Container → Template**, and pick **MoneySite** under
   **[ Default templates ]**.
2. Fill in:

   | Field | Value |
   |---|---|
   | Plaid Client ID / Plaid Secret | from the Plaid dashboard (Developers → Keys). To keep existing bank connections, use the **same** ones as your current copy. |
   | Plaid Environment | `production` for real banks (Plaid's Trial plan is production) |
   | Time Zone | yours, e.g. `America/Indiana/Indianapolis`. Without it "today" and month boundaries are in UTC. |

3. Click **Apply**, then open `http://<server-ip>:8080`.

To change settings later, use the container's icon → **Edit**, not Add Container. Add
Container always starts from the template's defaults.

## 3. Bring over your existing data (optional)

Skip this for a fresh start; connect banks from the **Accounts** page instead.

To move from another copy (for example the app on your PC), do it in the browser:

1. On the **old** copy, open **Accounts → Backup & restore → Download backup**.
2. Stop the old copy, and don't run it again (see below).
3. On the **new** copy, open **Accounts → Restore from backup…**, pick the file, and
   confirm.

Your banks keep working without re-linking, as long as the new copy uses the same Plaid
client ID, secret and environment. The data that was there before the restore is kept
next to the database as `money.before-restore-<date>.db`.

## Don't run two copies at the same time

Two copies with the same data both sync the same banks with the same access tokens, and
each keeps its own sync position, so they drift apart. Changes in one (categories, rules,
labels) don't appear in the other. Pick one live copy. To switch, back up from the old
one, restore into the new one, and stop the old one.

## Connecting banks from another device (HTTPS)

**Daily syncs don't need HTTPS.** They run on the server and talk to Plaid directly.

**Connect a bank**, **Enable investment tracking** and re-linking a bank open Plaid
Link in your browser. That **may not work** when the page is opened over plain
`http://<server-ip>:8080` from another device, because browsers only treat
`http://localhost` as a secure page. This has not been tested here. If Plaid Link fails,
put a local reverse proxy with a certificate in front of it (e.g. Nginx Proxy Manager or
Caddy on Unraid; it must pass WebSockets), and keep that proxy local only.

## Protect the data: it holds Plaid access tokens

- **Backups from the Accounts page and Unraid appdata backups contain everything,**
  including the Plaid access tokens. Keep them on the server or another trusted,
  encrypted place; don't sync them to a shared or public cloud folder.
- The container's variables hold your Plaid secret. Don't share screenshots of them.
- The image contains no database: `.dockerignore` excludes `*.db*`.
- To remove a Plaid connection for good, disconnect the bank in the app so the token is
  revoked at Plaid. Deleting the database alone does not revoke the token.

## Settings reference

| Variable | Default | Purpose |
|---|---|---|
| `Plaid__ClientID`, `Plaid__ClientSecret` | — | Plaid keys. The container doesn't read dotnet user-secrets. |
| `Plaid__Environment` | `sandbox` | `production` for real banks |
| `TZ` | UTC | Time zone for "today", daily balances and months |
| `PUID`, `PGID` | `99`, `100` | Owner of `/data` and the user the app runs as |
| `MONEYSITE_DB_PATH` | `/data/money.db` | Leave as is |

## Building it yourself

`docker-compose.yml` builds from source: copy the folder holding `Dockerfile` and
`MoneySite/` to the server and run `docker compose up -d --build` there, or point
Unraid's Compose Manager plugin at it. The published image
(`hintab/moneysite:latest` on Docker Hub, also `ghcr.io/hintab/moneysite:latest`) is
rebuilt and smoke-tested on every push to `main`.

## Notes

- SQLite and Unraid's `/mnt/user` layer: if you see "database is locked" or I/O errors,
  map `/mnt/cache/appdata/moneysite` (or your pool's path) instead of `/mnt/user/...`.
- On every start the app re-runs automatic categorization and its schema checks. Categories
  picked by hand are never changed.
