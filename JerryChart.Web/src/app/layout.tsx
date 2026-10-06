import type { Metadata } from "next";
import Link from "next/link";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  metadataBase: process.env.SITE_URL ? new URL(process.env.SITE_URL) : undefined,
  title: "Jerry No",
  description: "Statistics on Bluesky's collective failure to make Jerry Chen reconsider his choices",
  openGraph: {
    title: "Jerry No",
    description: "Bluesky's collective failure to make Jerry reconsider.",
    siteName: "Jerry No",
    type: "website",
  },
  twitter: {
    card: "summary_large_image",
    title: "Jerry No",
    description: "Bluesky's collective failure to make Jerry reconsider.",
  },
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col">
        {children}
        <footer className="mx-auto mt-auto w-full max-w-4xl px-6 py-6 text-right text-sm text-muted-foreground">
          <p className="mb-2">
            <a href="https://github.com/blowdart/JerryChart" className="underline underline-offset-4">
              Get the source code on GitHub
            </a>
          </p>
          <nav aria-label="Legal" className="flex flex-wrap justify-end gap-x-4 gap-y-2">
            <Link href="/terms" className="underline underline-offset-4">Terms of use</Link>
            <Link href="/privacy" className="underline underline-offset-4">Privacy policy</Link>
          </nav>
        </footer>
      </body>
    </html>
  );
}
