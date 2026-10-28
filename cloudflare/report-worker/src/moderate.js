// Pictures on public reports (4.1.2): only family-friendly screenshots of an app or an error message. Each picture is
// looked at by a vision model on Cloudflare's free Workers AI; anything that isn't clearly such a screenshot - a photo,
// a meme, nudity, gore, hate symbols, bad words on screen - is left out of the report. If the check can't run (free
// daily allowance used up, model busy) the picture is left out too: nothing unchecked is ever shown.
// Hidden details inside the file (camera, place, the PC's user name in text chunks) are removed before it's kept.

import { censor } from "./censor.js";

export const MODEL = "@cf/meta/llama-4-scout-17b-16e-instruct";

const ASK = `You check pictures attached to bug reports for StreamLite, a Windows audio and camera app. The pictures are shown publicly, so they must be family friendly.
Reply with only this JSON, nothing else:
{"screenshot": true or false, "safe": true or false, "text": "the words you can read in the picture, up to 300 characters"}
"screenshot" is true only if the picture is a screenshot of a computer screen: an app window, settings, a desktop, a game, or an error message. It is false for photos (including photos of a screen taken with a phone), memes, drawings and anything else.
"safe" is false if the picture shows any nudity, sexual content, violence, blood or gore, weapons aimed at people, drugs, hate symbols, rude gestures, or rude, sexual or hateful words. When unsure, "safe" is false.`;

/** Looks at one picture: { ok: true } or { ok: false, why } with why = "not a screenshot" | "not family friendly" | "unchecked". */
export async function checkPicture(env, base64, ext) {
  if (!env.AI) return { ok: false, why: "unchecked" };
  try {
    const res = await env.AI.run(MODEL, {
      messages: [
        { role: "system", content: ASK },
        { role: "user", content: [{ type: "text", text: "Check this picture." }, { type: "image_url", image_url: { url: `data:image/${ext === "jpg" ? "jpeg" : "png"};base64,${base64}` } }] },
      ],
      max_tokens: 300,
      temperature: 0,
    });
    return verdict(res && (res.response !== undefined ? res.response : res.choices && res.choices[0] && res.choices[0].message && res.choices[0].message.content));
  } catch {
    return { ok: false, why: "unchecked" };
  }
}

/** The model's answer -> keep or leave out. Anything but a clear "screenshot and safe" leaves it out. */
export function verdict(answer) {
  let v = answer;
  if (typeof v === "string") {
    const a = v.indexOf("{"), b = v.lastIndexOf("}");
    if (a < 0 || b <= a) return { ok: false, why: "unchecked" };
    try { v = JSON.parse(v.slice(a, b + 1)); } catch { return { ok: false, why: "unchecked" }; }
  }
  if (!v || typeof v !== "object") return { ok: false, why: "unchecked" };
  if (v.screenshot !== true) return { ok: false, why: "not a screenshot" };
  if (v.safe !== true) return { ok: false, why: "not family friendly" };
  // Words on the screen get the same check as the report's own text.
  if (typeof v.text === "string" && censor(v.text) !== v.text) return { ok: false, why: "not family friendly" };
  return { ok: true };
}

// ---------------------------------------------------------------- hidden details out

const PNG_KEEP = new Set(["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "sRGB"]);

/** The picture with only what's needed to show it: PNG keeps the image chunks, JPEG drops EXIF/XMP/comments. null = broken. */
export function stripDetails(bytes, ext) {
  try { return ext === "png" ? stripPng(bytes) : stripJpeg(bytes); } catch { return null; }
}

function stripPng(b) {
  const out = [b.subarray(0, 8)];
  let i = 8, sawEnd = false;
  while (i + 12 <= b.length) {
    const len = ((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]) >>> 0;
    const type = String.fromCharCode(b[i + 4], b[i + 5], b[i + 6], b[i + 7]);
    const end = i + 12 + len;
    if (end > b.length) return null;
    if (PNG_KEEP.has(type)) out.push(b.subarray(i, end));
    i = end;
    if (type === "IEND") { sawEnd = true; break; }
  }
  return sawEnd ? join(out) : null;
}

function stripJpeg(b) {
  if (b[0] !== 0xff || b[1] !== 0xd8) return null;
  const out = [b.subarray(0, 2)];
  let i = 2;
  while (i + 4 <= b.length) {
    if (b[i] !== 0xff) return null;
    const m = b[i + 1];
    if (m === 0xff) { i++; continue; }
    if (m === 0xda) { out.push(b.subarray(i)); return join(out); }   // start of the image data: the rest is the picture
    const len = (b[i + 2] << 8) | b[i + 3];
    const end = i + 2 + len;
    if (len < 2 || end > b.length) return null;
    // APP1-APP13 and APP15 (EXIF, XMP, Photoshop, ...) and comments go; APP0 (JFIF) and APP14 (Adobe colours) stay.
    const drop = (m >= 0xe1 && m <= 0xed) || m === 0xef || m === 0xfe;
    if (!drop) out.push(b.subarray(i, end));
    i = end;
  }
  return null;
}

function join(parts) {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

export const toBase64 = (bytes) => {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
};
export const fromBase64 = (b64) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
