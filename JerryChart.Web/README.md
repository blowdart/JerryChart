# JerryChart frontend

Next.js App Router with TypeScript, Tailwind CSS, and generated shadcn/ui
components. See the [solution README](..\README.md) for Aspire startup and
connection configuration.

The frontend renders Jerry no summaries, top reply authors, and monthly reply
charts through server components. `API_BASE_URL` is supplied by Aspire and never exposed to the
browser. MySQL is accessible only through the .NET API.
The last-detected timestamp tracks only matching replies directly to Jerry Chen
and links to that reply on Bluesky when its AT URI is available. Replies to other
accounts still contribute to the separate wrong-Jerry counter, but never advance
this timestamp. The time remains the persisted ingestion time, not the post's
creation time. Existing timestamps for other accounts, missing replies, or
unclassified legacy timestamps without a URI are not displayed; historical
per-reply ingestion times were not stored, so the next new qualifying reply
establishes a known timestamp rather than inventing one from posting times.
Hovering over or keyboard-focusing that link opens an official Bluesky embed.
This preview performs no server-side or idle-page embed loading, post fetch,
Open Graph scrape, or third-party script loading. The iframe is mounted only while the preview is open,
and is removed on close, so reopening asks Bluesky again rather than using a
JerryChart content cache. Link navigation and the timestamp remain unchanged.
The preview has a close button, supports Escape outside the cross-origin iframe,
and retains a direct link if the embed is blocked or unavailable.
It uses a nonmodal Base UI Popover with managed portal tab order rather than
PreviewCard, whose content is not keyboard-navigable. Opening leaves focus on
the timestamp; Tab enters the close button, scroll region, embedded document,
and direct link, then continues to the next page control. Shift+Tab reverses
that order. Closing restores focus without reopening the popup, and Enter or
click on the timestamp still navigates to the post.
Image posts resize as Bluesky loads their media. Tall previews scroll within a
viewport-bounded, keyboard-focusable region instead of growing over the page;
the close button and direct link stay outside that region. On very narrow
screens the region also scrolls horizontally to accommodate Bluesky's minimum
300-pixel embed width.

### Reply preview privacy

Bluesky's official embed checks `post.author.labels` for `!no-unauthenticated`
**before** rendering the post, substituting an external-site opt-out notice.
Quoted posts are separately checked via `record.author.labels`, including quotes
with media. This is author-level metadata: checking only `post.labels` is not
sufficient. JerryChart does not render any post text, author details, media, or
Open Graph fallback alongside the embed, nor pass Bluesky credentials.

Verified on October 20, 2026 against Bluesky's
[embed screen](https://github.com/bluesky-social/social-app/blob/5572bef8b22cfc7b4d8b5989e72ccf578ba942ff/bskyembed/src/screens/post.tsx)
and [quoted-record renderer](https://github.com/bluesky-social/social-app/blob/5572bef8b22cfc7b4d8b5989e72ccf578ba942ff/bskyembed/src/components/embed.tsx).
The deployed `embed.bsky.app` entry bundle (`post-D8-Hi36E.js`) also checks
`post.author.labels` before rendering, and its imported `logo-WhKaIFp7.js`
bundle checks quoted-record author labels. Kate Bevan's example
(`did:plc:erlxz4sppc5uv7m4efmcvpsf`, post `3my5n4tyxfcud`) returns the opt-out
in the author's labels, with an empty post-label array; its live embed displays
only the opt-out notice, Bluesky branding, and navigation, not the post or avatar.
The public API still
returns its content; availability through that API is not permission to display
it. Privacy enforcement inside the cross-origin frame remains Bluesky's
responsibility and should be rechecked if their embed implementation changes.

The iframe uses `no-referrer` and does not pass the optional referring-page
parameter. Resize messages must match its origin, source window, and instance
ID; heights are finite and bounded. Opening it still sends the visitor's IP
address and requested post to Bluesky; see the site's privacy policy.
Loading completes only after a validated resize message confirms that the
embed rendered, including its privacy notice. An iframe `load` event alone
does not cancel the timeout; a blocked or uninitialized embed reports a failure
after 15 seconds while preserving the direct link.

The top-five posts card opens a ranked dialog of all resolved Jerry posts with
recorded matching replies. Bluesky post text loads in sequential batches of five,
with each batch displayed immediately. Completed batches remain visible if a
later request fails; the dialog reports the failure and offers a retry.
Completed batches are cached in the owning card's memory across dialog openings.
Retries resume after cached batches. A changed ranked URI list invalidates the
cache, and reloading the page clears it; nothing is stored in browser storage.

Profile hover cards display Bluesky's verification badge beside the name only
when the public profile reports a valid verified status. A domain handle or
trusted-verifier status alone does not grant this badge. Profiles use the existing
15-minute cache; verification changes appear when the profile is fetched again
after expiry. Badge artwork is from Bluesky's MIT-licensed client; see
`THIRD-PARTY-NOTICES.txt`.

Profile descriptions link explicit HTTP/HTTPS URLs and bare domains with public
top-level suffixes using `linkify-it` and `tlds`. Bare domains open over HTTPS;
email addresses stay as text, and Bluesky mentions retain account-resolution
checks. Recognizing an address does not establish that the site exists or is safe.

## Link previews

Open Graph and Twitter cards use a static 1200 x 630 branded PNG generated by
Next.js at build time, with decorative bars rather than live statistics or an
account avatar. No statistics API or external image request is needed.

When deploying, set `SITE_URL` to the public HTTPS origin (for example,
`https://your-domain.example`) before building so card image URLs resolve to the
public site. Until a domain is chosen, leave it unset for Next.js's development
URL defaults. Localhost links cannot be fetched by external preview services.

## Visual Studio

`JerryChart.Web.esproj` represents the frontend in `JerryChart.slnx`, automatically
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
npm run test:browser
```

The real-component browser regression uses Playwright with an isolated local
fixture and intercepted Bluesky responses, not the running Aspire application
or external post content. On Windows it uses installed Microsoft Edge by default;
elsewhere run `npx playwright install --with-deps chromium` first. Set
`BROWSER_CHANNEL` to choose another installed browser channel. CI runs this
regression with Chromium and retains its JUnit results alongside the Node tests.

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
