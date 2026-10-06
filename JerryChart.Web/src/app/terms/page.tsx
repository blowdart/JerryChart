import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Terms of use | Jerry No",
  description: "A few ground rules for using Jerry No.",
};

export default function TermsOfUse() {
  return (
    <main className="mx-auto w-full max-w-3xl space-y-8 px-6 py-12">
      <header className="space-y-2">
        <h1 className="text-4xl font-semibold tracking-tight">Terms of use</h1>
        <p className="text-sm text-muted-foreground">Last updated: October 6, 2026</p>
        <p>
          Jerry No is a free personal project run by Barry Dorrans, for
          entertainment and curiosity. It is not affiliated with or endorsed by
          Bluesky or the people mentioned on the site.
        </p>
      </header>

      <section aria-labelledby="accuracy" className="space-y-3">
        <h2 id="accuracy" className="text-2xl font-semibold">Take the numbers lightly</h2>
        <p>
          Statistics may be incomplete, delayed, or inaccurate. Historical counts
          may include posts or accounts that are no longer available, and cached
          handles and account statuses may be out of date. Rankings are counts,
          not judgments about anyone or evidence of their intentions.
        </p>
      </section>

      <section aria-labelledby="respect" className="space-y-3">
        <h2 id="respect" className="text-2xl font-semibold">Be kind, and be considerate</h2>
        <p>
          Do not use this site to harass, threaten, or target anyone. Please do
          not disrupt the service or overwhelm it with automated requests.
          Public access to data does not give you ownership of other
          people&apos;s content.
        </p>
      </section>

      <section aria-labelledby="availability" className="space-y-3">
        <h2 id="availability" className="text-2xl font-semibold">No promises of availability</h2>
        <p>
          The site is provided as it is, without a promise of accuracy,
          availability, or continued operation. Features may change and the site
          may go offline. Nothing here limits rights or protections that cannot
          legally be excluded.
        </p>
      </section>

      <section aria-labelledby="privacy-contact" className="space-y-3">
        <h2 id="privacy-contact" className="text-2xl font-semibold">Privacy and questions</h2>
        <p>
          See the{" "}
          <Link href="/privacy" className="underline underline-offset-4">privacy policy</Link>{" "}
          for how data is used. For questions or corrections, contact{" "}
          <a href="https://bsky.app/profile/blowdart.me" className="underline underline-offset-4">
            @blowdart.me
          </a>{" "}
          on Bluesky. These terms may change; the date above identifies the
          current version.
        </p>
      </section>

      <Link href="/" className="inline-block underline underline-offset-4">Back to statistics</Link>
    </main>
  );
}
