import { ImageResponse } from "next/og";

export const alt = "Jerry No. Bluesky's collective failure to make Jerry reconsider. Decorative bar chart.";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export default function SocialImage() {
  return new ImageResponse(
    <div style={{
      display: "flex", width: "100%", height: "100%", background: "#fafafa",
      color: "#171717", padding: 72, flexDirection: "column", position: "relative",
    }}>
      <div style={{
        display: "flex", position: "absolute", bottom: 0, right: 48,
        alignItems: "flex-end", gap: 18, opacity: 0.12,
      }}>
        {[90, 150, 120, 220, 180, 290, 250, 370].map((height, index) => (
          <div key={index} style={{
            display: "flex", width: 58, height, background: "#2563eb",
            borderRadius: "12px 12px 0 0",
          }} />
        ))}
      </div>
      <div style={{ display: "flex", fontSize: 26, color: "#525252", marginBottom: 34 }}>
        BLUESKY REPLY STATISTICS
      </div>
      <div style={{ display: "flex", fontSize: 116, fontWeight: 700, letterSpacing: -6 }}>
        Jerry No
      </div>
      <div style={{
        display: "flex", fontSize: 38, lineHeight: 1.35, maxWidth: 760, marginTop: 28,
      }}>
        Bluesky&apos;s collective failure to make Jerry reconsider.
      </div>
    </div>,
    size,
  );
}
