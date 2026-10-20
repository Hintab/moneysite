> **[DRAFT — PENDING REVIEW]**

# MoneySite

A personal money manager that syncs your bank, credit card and investment accounts
through [Plaid](https://plaid.com). Built with Blazor Server on .NET 8, with all data
stored in a local SQLite database.

- **Income vs Spending:** monthly totals, categories, recurring bills, top companies.
- **Net Worth:** balances per account, history chart, retirement and investment holdings.
- **Transactions:** search, filter, and categorize with keyword rules.
- **Credit cards:** what was bought, paid and owed each month.

## Security

- **There is no login.** Anyone who can reach the site can see everything and disconnect
  banks. Run it on your own computer or home network only. Never expose it to the internet.
- `money.db` holds your transactions and Plaid access tokens. It is never committed
  (see `.gitignore`) and never copied into the Docker image (see `.dockerignore`).
- Plaid keys come from dotnet user-secrets (local) or environment variables (Docker).
  Never put them in `appsettings.json`.

## Run locally

```bash
cd MoneySite
dotnet user-secrets set "Plaid:ClientID" "your-client-id"
dotnet user-secrets set "Plaid:ClientSecret" "your-secret"
dotnet user-secrets set "Plaid:Environment" "production"
dotnet run
```

## Run in Docker / Unraid

The image is published as `ghcr.io/hintab/moneysite:latest` on every push to `main`, and
an Unraid template is in [Hintab/docker_templates](https://github.com/Hintab/docker_templates).
See [DOCKER.md](DOCKER.md) for the full setup, including moving an existing database.

## License

MIT, see [LICENSE](LICENSE).
