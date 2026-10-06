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
  title: "Jerry No",
  description: "Statistics on Bluesky's collective failure to make Jerry Chen reconsider his choices",
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
          <nav aria-label="Legal" className="flex flex-wrap justify-end gap-x-4 gap-y-2">
            <Link href="/terms" className="underline underline-offset-4">Terms of use</Link>
            <Link href="/privacy" className="underline underline-offset-4">Privacy policy</Link>
          </nav>
        </footer>
      </body>
    </html>
  );
}
