export function profileDisplayText(text: string): string {
  const withoutDirectionControls = text.replace(/[\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069]/gu, "");
  let marks = 0;
  let result = "";
  for (const character of withoutDirectionControls.normalize("NFC")) {
    if (/\p{M}/u.test(character)) {
      // Keep ordinary accents and script marks, but bound decorative vertical stacking.
      if (++marks <= 3) result += character;
    } else {
      marks = 0;
      result += character;
    }
  }
  return result;
}
