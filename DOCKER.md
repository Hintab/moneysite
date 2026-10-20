> **[DRAFT — PENDING REVIEW]**

# Running MoneySite in Docker on Unraid

The image holds only the app. All your data (Plaid access tokens, sync cursors,
categories, keyword rules, merchant groupings, account labels, balance snapshots) lives
in one SQLite database, `money.db`. On Unraid it is kept in
`/mnt/user/appdata/moneysite`, which the container sees as `/data`.

Using the same Plaid keys does **not** download everything again. A full download only
happens if the container starts with an empty database. Copy your existing `money.db`
over first (step 3) and your linked banks keep working without re-linking. The next
sync only fetches what's new since the last one.

## ⚠ Security warning: the app has no login

Anyone who can reach the app's address can see every transaction and balance, and can
**disconnect your banks**. There is no password screen.

- Keep it reachable only from your home network. For access away from home, use a VPN
  (for example Tailscale), or a reverse proxy that requires a login.
- **Never port-forward it on your router** or expose it through a public tunnel.
- `docker-compose.yml` publishes port 8080 to the LAN (`"8080:8080"`). To limit it to
  the server itself, change that to `"127.0.0.1:8080:8080"`.

## 1. Stop the app on the Windows PC

Stop the app everywhere it is running (Visual Studio debugging, `dotnet run`, a
published copy). In PowerShell on the PC, check that nothing is left:

```powershell
Get-Process MoneySite -ErrorAction SilentlyContinue
```

This should print nothing. Stopping it lets SQLite write the `-wal` file back into
`money.db`, and it makes sure the files don't change while you copy them.

From now on, **don't run the Windows copy and the Unraid copy at the same time** (see
below).

## 2. Prepare the data folder on Unraid

In the Unraid terminal (or over SSH):

```bash
mkdir -p /mnt/user/appdata/moneysite
```

If you already started the container once with an empty database, stop it and remove
the empty database first. Otherwise its leftover `-wal` file could be mixed with your
real `money.db`:

```bash
rm -f /mnt/user/appdata/moneysite/money.db /mnt/user/appdata/moneysite/money.db-wal /mnt/user/appdata/moneysite/money.db-shm
```

## 3. Copy the database from the PC

Copy `money.db` **together with** `money.db-wal` and `money.db-shm` if they exist.
Recent changes may still be sitting in the `-wal` file, and copying `money.db` alone
would lose them.

From PowerShell on the PC, in the solution folder (the one holding `MoneySite.sln`).
Replace `tower` with your server's name or IP address:

```powershell
$files = Get-Item MoneySite\Data\money.db, MoneySite\Data\money.db-wal, MoneySite\Data\money.db-shm -ErrorAction SilentlyContinue
$files | Select-Object Name, Length
scp $files.FullName root@tower:/mnt/user/appdata/moneysite/
```

Windows 11 includes `scp`. SSH must be enabled on the server, under **Settings →
Management Access**. Copying over an SMB share works too, if your `appdata`
share is exported.

Then, on Unraid, hand the files to the container's non-root `app` user (UID 1654).
Without this the app can't write to the database:

```bash
chown -R 1654:1654 /mnt/user/appdata/moneysite
ls -l /mnt/user/appdata/moneysite
```

`money.db` should be roughly the same size as on the PC. Keep the PC's copy as a backup
until you have confirmed the Unraid copy works. Older backups such as
`money.backup-*.db` are not needed on the server.

## 4. Fill in the settings

You need these values, the **same** ones the Windows app uses:

| Variable | Value |
|---|---|
| `Plaid__ClientID` | your Plaid client ID |
| `Plaid__ClientSecret` | your Plaid secret |
| `Plaid__Environment` | same as on the PC. Yours is `trial`, which the app treats as `production`. |
| `MONEYSITE_DB_PATH` | `/data/money.db` |
| `TZ` | your time zone, e.g. `America/New_York` |

To see the Plaid values, run this on the PC in your own terminal:

```powershell
dotnet user-secrets list --project MoneySite
```

- The access tokens in `money.db` only work with the same client ID and environment
  they were created with. Different values mean every bank needs re-linking.
- The container runs as Production, which does **not** load user-secrets. That's why
  the keys must come in as environment variables.
- Without `TZ` the container runs in UTC, which shifts "today", daily balance
  snapshots and month boundaries.

## 5. Run it on Unraid

### Option A: Unraid template (recommended)

Every push to `main` on GitHub builds the image `ghcr.io/hintab/moneysite:latest`
(see `.github/workflows/docker-image.yml`), and the template in
[Hintab/docker_templates](https://github.com/Hintab/docker_templates) pulls it.

1. Under **Docker**, at the bottom of the page, find **Template repositories**, add
   `https://github.com/Hintab/docker_templates` and save.
2. Choose **Add Container**, pick **MoneySite** from the template list.
3. Fill in the Plaid values and time zone (step 4) and apply.

Updates: push to GitHub, wait for the build to finish (the repo's **Actions** tab), then
choose **Check for updates** on Unraid's Docker page.

### Building it on the server instead

Copy the solution folder (the one with `Dockerfile`, `docker-compose.yml` and
`MoneySite/`) to the server, for example to `/mnt/user/appdata/moneysite-src`. Leave out
`bin`, `obj` and `MoneySite\Data\*.db*`. Then use Option B or C.

### Option B: Compose Manager plugin

1. Install the **Compose Manager** plugin from Community Applications.
2. Under **Docker → Compose**, add a new stack named `moneysite`.
3. Edit the stack's compose file and paste in `docker-compose.yml`. Change
   `build: .` to the folder you copied, e.g. `build: /mnt/user/appdata/moneysite-src`.
4. Edit the stack's ENV file and paste in the contents of `.env.example` with your real
   values (step 4).
5. Choose **Compose Up**. The first start builds the image, which takes a few minutes.

### Option C: "Add Container" with a local build

1. Build the image in the Unraid terminal:

   ```bash
   docker build -t moneysite:latest /mnt/user/appdata/moneysite-src
   ```

2. Under **Docker → Add Container**, set:
   - **Repository:** `moneysite:latest`
   - **Port:** container `8080` → host `8080`
   - **Path:** container `/data` → host `/mnt/user/appdata/moneysite`
   - **Variables:** `Plaid__ClientID`, `Plaid__ClientSecret`, `Plaid__Environment`,
     `MONEYSITE_DB_PATH`, `TZ` (step 4)
3. Apply.

Unraid's update check may show this image as "not available", because it exists only
on the server. That doesn't stop it from running. If the container won't create
because Unraid tries to pull the image from a registry, use Option B instead.

### Check it

Open `http://<server-ip>:8080` from a device on your home network. Your accounts,
categories and history should all be there, and the Accounts page should list your
linked banks. The background worker syncs each bank every 24 hours, same as before.

## Don't run both copies at the same time

After the move, the Unraid database and the PC's `MoneySite\Data\money.db` are two
separate copies. If both run:

- both sync the same banks with the same access tokens, and each keeps its own cursor,
  so they drift apart;
- anything you change in one (categories, rules, labels) does not appear in the other.

Treat Unraid as the live copy. To use the PC again (for example to re-link a bank, see
below):

1. Stop the container.
2. Copy `money.db` and its `-wal`/`-shm` files back to the PC.
3. Run the app on the PC.
4. Stop it, then copy the files back to the server the same way as in step 3, including
   the `chown`.

## Connecting banks from another device (HTTPS)

**Daily syncs don't need HTTPS.** They run on the server and talk to Plaid directly.

**Connect a bank**, **Enable investment tracking** and re-linking a bank open Plaid
Link in your browser. That **may not work** when the page is opened over plain
`http://<server-ip>:8080` from another device. Browsers only treat `http://localhost`
as a secure page, and Plaid Link may need one, especially for banks that sign in
through their own website (OAuth). This has not been tested here. If Plaid Link fails
to open or errors out, you have two options:

- **A local reverse proxy with a certificate** on Unraid, e.g. Nginx Proxy Manager or
  Caddy. It must pass WebSockets (Blazor Server needs them). Keep it local only, and
  never port-forward it.
- **Re-link on the PC**: stop the container, run the app on the PC at
  `https://localhost`, make the change, then move the database back (see "Don't run
  both copies at the same time").

## Protect the data: it holds Plaid access tokens

Anyone who can read `money.db`, and also has your Plaid client ID and secret, can pull
your bank data.

- **Unraid appdata backups include `money.db`.** That includes the Appdata Backup
  plugin and any copies of the `appdata` share. Keep those backups on the server or
  another trusted, encrypted location. Don't sync them to a shared or public cloud
  folder. Let the backup stop the container while it runs, so the `-wal` file is
  written back first.
- The `money.backup-*.db` files on the PC (and their `-wal`/`-shm` files) hold the
  **same Plaid access tokens**. Protect them the same way, and delete the ones you no
  longer need.
- The ENV file and the container's variables hold your Plaid secret. Don't share
  screenshots of them.
- The image contains no database, because `.dockerignore` excludes `*.db*`.
- To remove a Plaid connection for good, disconnect the bank in the app so the token is
  revoked at Plaid. Deleting the database alone does not revoke the token.

## Notes

- SQLite and Unraid's `/mnt/user` layer: if you see "database is locked" or I/O errors,
  map `/mnt/cache/appdata/moneysite` (or your pool's path) instead of `/mnt/user/...`.
  Another option is to set the `appdata` share to exclusive access. This is common
  advice for SQLite-based containers on Unraid.
- On every start the app re-runs automatic categorization and `DbSeed` schema checks.
  This is the same as running locally and does not touch categories picked by hand.
- You may see a warning about ASP.NET Core data-protection keys not being saved outside
  the container. It only means you may have to reload open browser tabs after the
  container restarts.
