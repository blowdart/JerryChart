# JerryChart frontend

Next.js App Router with TypeScript, Tailwind CSS, and generated shadcn/ui
components. See the [solution README](..\README.md) for Aspire startup and
connection configuration.

The frontend renders Jerry no summaries, top reply authors, and monthly reply
charts through server components. `API_BASE_URL` is supplied by Aspire and never exposed to the
browser. MySQL is accessible only through the .NET API.

## Visual Studio

`JerryChart.Web.esproj` represents the frontend in `JerryChart.sln`, automatically
showing its source, tests, configuration, and assets. Install Visual Studio's
JavaScript/TypeScript project-system support to open it. Generated output and
dependency directories are excluded from the project view.

The project is excluded from all solution build configurations and disables
automatic npm installation and build scripts. Aspire still owns frontend
installation/startup, and the dedicated frontend CI job still runs validation.
Keep AppHost as the startup project when running the complete application.
Starting the frontend project alone uses `npm run dev` and requires dependencies
and `API_BASE_URL` to be configured first.

```powershell
npm ci
npm run lint
npm run typecheck
npm run build
```

For standalone development, set `API_BASE_URL` to the API's HTTP endpoint, then
run `npm run dev`. Existing generated components and `components.json` are
retained. Use Node 26, matching the CI runtime and Node type definitions.
The shadcn 4 CLI is available for adding components. TypeScript 7 handles
application type-checking and builds; the separately locked `tooling/lint`
package supplies ESLint 9, Next.js's flat config, and TypeScript 6 for lint
plugins that still require the JavaScript compiler API. `npm ci` installs both
dependency trees through the frontend postinstall.

Modern shadcn and Next.js lint tooling carry an accepted developer-tool-only
`braces` denial-of-service advisory. See the solution README for the advisory
and precautions; these upgrades are not an audit-clean dependency set.
