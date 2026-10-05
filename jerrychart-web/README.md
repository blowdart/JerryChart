# JerryChart frontend

Next.js App Router with TypeScript, Tailwind CSS, and generated shadcn/ui
components. See the [solution README](..\README.md) for Aspire startup and
connection configuration.

The frontend renders Jerry no summaries, top reply authors, and monthly reply
charts through server components. `API_BASE_URL` is supplied by Aspire and never exposed to the
browser. MySQL is accessible only through the .NET API.

```powershell
npm ci
npm run lint
npm run build
```

For standalone development, set `API_BASE_URL` to the API's HTTP endpoint, then
run `npm run dev`. Existing generated components and `components.json` are
retained. The audit-suggested downgrade pins `shadcn` to 1.0.0, which is a
placeholder package without a CLI. Do not use `npx shadcn add` with this
configuration; adding components requires revisiting that tooling downgrade.
