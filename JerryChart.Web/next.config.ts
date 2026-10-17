import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  experimental: {
    // Avoid the Next.js 16.4 restart/HMR loop: https://github.com/vercel/next.js/issues/100251
    turbopackFileSystemCacheForDev: false,
  },
};

export default nextConfig;
