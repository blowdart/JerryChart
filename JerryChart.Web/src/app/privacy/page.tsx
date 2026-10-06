import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Privacy policy | Jerry No",
  description: "How Jerry No uses public Bluesky data and information from website visitors.",
};

export default function PrivacyPolicy() {
  return (
    <main className="mx-auto w-full max-w-3xl space-y-8 px-6 py-12">
      <header className="space-y-2">
        <h1 className="text-4xl font-semibold tracking-tight">Privacy policy</h1>
        <p className="text-sm text-muted-foreground">Last updated: October 6, 2026</p>
        <p>
          Jerry No is a personal project run by Barry Dorrans. It counts public
          Bluesky replies containing &quot;Jerry no&quot; and displays statistics
          about those replies. It is not operated by Bluesky.
        </p>
      </header>

      <section aria-labelledby="public-data" className="space-y-3">
        <h2 id="public-data" className="text-2xl font-semibold">Public Bluesky data</h2>
        <p>
          Bluesky posts and public account information are public data, available
          to anyone through the AT Protocol, not just people logged into Bluesky.
          This project processes that public data; it does not access private
          messages or other non-public account information.
        </p>
        <p>
          The monitor reads public AT Protocol data through Jetstream archive and
          live streams. For matching replies, it stores post identifiers, posting
          times, author DIDs (stable account identifiers), and parent-post and
          parent-author identifiers. It also caches public handles and, when a
          handle cannot be resolved, account status reported by a relay.
        </p>
        <p>
          This data is used to calculate reply counts, author rankings, and
          monthly statistics. Author identifiers, available handles, and account
          status labels appear in public reports. The statistics database stores
          identifiers and metadata, not the full text of matching replies.
        </p>
        <p>
          No AI is used to process this data. Matching replies and calculating
          statistics use ordinary code and database queries, not AI models.
          This project does not send Bluesky data to AI services or use it to
          train AI models.
        </p>
        <p>
          Historical records currently have no automatic expiry. Deleting a post
          or closing an account on Bluesky does not automatically remove its
          previously recorded contribution from these statistics. Handles and
          account statuses are refreshed periodically, but may be out of date.
        </p>
      </section>

      <section aria-labelledby="visitors" className="space-y-3">
        <h2 id="visitors" className="text-2xl font-semibold">Visiting this website</h2>
        <p>
          You do not need an account or a Bluesky login to use this site. The
          application does not set analytics or advertising cookies, use
          browser local storage for tracking, or include third-party analytics.
          Search text in the author list is processed in your browser.
        </p>
        <p>
          Like other websites, requests expose information such as your IP
          address, browser information, requested URL, and request time to the
          server and any hosting infrastructure. Operational logs and telemetry
          may record request details and errors for reliability and diagnosis;
          this policy does not promise that hosting logs are absent or have a
          particular retention period.
        </p>
      </section>

      <section aria-labelledby="third-parties" className="space-y-3">
        <h2 id="third-parties" className="text-2xl font-semibold">Requests to Bluesky</h2>
        <p>
          Your browser contacts Bluesky&apos;s public API to retrieve displayed
          post text and, when you open a profile preview, profile information.
          It may also resolve handles mentioned in displayed profile text.
          Public profile images are loaded from Bluesky&apos;s CDN. These
          requests disclose your IP address and the requested post, profile, or
          handle to those services. API lookups do not send Bluesky login
          credentials and use short-lived, in-memory caches.
        </p>
        <p>
          Following a Bluesky profile or post link takes you to another service,
          whose own privacy policy applies. See{" "}
          <a className="underline underline-offset-4" href="https://bsky.social/about/support/privacy-policy">
            Bluesky&apos;s privacy policy
          </a>.
        </p>
      </section>

      <section aria-labelledby="contact" className="space-y-3">
        <h2 id="contact" className="text-2xl font-semibold">Questions and requests</h2>
        <p>
          Contact Barry Dorrans on Bluesky at{" "}
          <a className="underline underline-offset-4" href="https://bsky.app/profile/blowdart.me">
            @blowdart.me
          </a>{" "}
          with privacy questions or requests concerning your recorded data.
          Include your account DID or a relevant post link so the records can be
          identified. Do not send passwords or other secrets. If you contact
          me publicly, your message will also be public.
        </p>
        <p>
          The operator can exclude your DID from this project, deleting stored
          replies authored by or addressed to it and removing its cached identity
          data. The DID itself is retained in an exclusion list to prevent future
          ingestion. Exclusion affects the active database; it cannot remove
          copies already downloaded by others, earlier diagnostic logs, or
          separately managed backups.
        </p>
        <p>
          This policy may change as the project changes. The update date above
          identifies the current version.
        </p>
      </section>

      <Link href="/" className="inline-block underline underline-offset-4">Back to statistics</Link>
    </main>
  );
}
