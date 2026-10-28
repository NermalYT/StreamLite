// StreamLite's bug reports (4.1.2): the app sends a report here - no account needed - and this Worker posts it on
// StreamLite's public issue list as the "StreamLite Reports" GitHub App. Bad language is starred out (censor.js);
// screenshots are shown on the report only after a check that they're family-friendly screenshots of an app or an
// error (moderate.js) - they're kept here and shown from /p/<id>. Videos aren't taken: the reporter ticks "video proof"
// and the developer asks for it on Discord. The reporter's Discord name goes only to a PRIVATE repository; the public
// issue just says a contact was left. Nothing a report contains is logged here. Everything runs on Cloudflare's free plan.
//
// Secrets (wrangler secret put): APP_ID, APP_PRIVATE_KEY (PKCS#8 PEM).   Vars (wrangler.toml): PUBLIC_REPO, PRIVATE_REPO.
// KV: RATE (spam counters, they expire by themselves), PICS (checked screenshots).   AI: Workers AI (the picture check).

import { censor } from "./censor.js";
import { checkPicture, stripDetails, toBase64, fromBase64 } from "./moderate.js";

const KINDS = ["Bug", "Broken feature", "Missing device / headphones", "Hardware incompatibility", "Idea to improve StreamLite"];
const AREAS = ["Microphone / noise removal", "Headphone EQ / correction / profiles", "3D game audio", "Camera (Windows)", "Installing / updating / setup", "Something else"];
// perDay is per sender; allDay caps everyone together so the free plan's daily allowances are never run out.
export const LIMITS = { title: 120, what: 8000, steps: 4000, details: 60000, version: 40, system: 200, images: 4, imageBytes: 3 * 1024 * 1024, body: 18 * 1024 * 1024, perHour: 5, perDay: 15, allDay: 100 };
const TYPES = { png: "image/png", jpg: "image/jpeg" };

const json = (status, obj) => new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json" } });

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/") return json(200, { ok: true, service: "StreamLite bug reports" });
    if (request.method === "GET" && url.pathname.startsWith("/p/")) return showPicture(env, url.pathname.slice(3));
    if (request.method !== "POST" || url.pathname !== "/report") return json(404, { error: "Not here." });
    const len = Number(request.headers.get("content-length") || 0);
    if (len > LIMITS.body) return json(413, { error: "The report is too big - fewer or smaller pictures, please." });

    let report;
    try { report = await request.json(); } catch { return json(400, { error: "That wasn't a report." }); }
    const r = validate(report);
    if (r.error) return json(400, { error: r.error });

    // Spam: a few reports an hour and a day from one sender (by network address; the counters expire on their own).
    const who = request.headers.get("cf-connecting-ip") || "unknown";
    const limited = await overLimit(env, who);
    if (limited) return json(429, { error: limited });

    // Screenshots: checked one by one; only the ones that pass are kept and shown.
    const shown = [];
    let leftOut = 0;
    for (const img of r.images) {
      const ok = env.PICS && (await checkPicture(env, toBase64(img.bytes), img.ext)).ok;
      if (!ok) { leftOut++; continue; }
      const id = randomId() + "." + img.ext;
      try { await env.PICS.put(id, img.bytes, { metadata: { type: TYPES[img.ext] } }); shown.push(`${url.origin}/p/${id}`); } catch { leftOut++; }
    }

    try {
      const app = await appToken(env);
      const { body, title, label } = publicIssue(r, shown, leftOut);
      const pub = await gh(env, app.token(env.PUBLIC_REPO, { issues: "write" }), "POST", `/repos/${env.PUBLIC_REPO}/issues`, { title, body, labels: [label] });
      if (r.discord) {
        const priv = app.token(env.PRIVATE_REPO, { issues: "write" });
        await gh(env, priv, "POST", `/repos/${env.PRIVATE_REPO}/issues`, { title: `#${pub.number}: ${title}`, body: privateNote(r, pub) });
      }
      return json(200, { number: pub.number, url: pub.html_url, leftOut });
    } catch (e) {
      return json(502, { error: "GitHub didn't take the report right now - please try again in a minute." });
    }
  },
};

async function showPicture(env, id) {
  if (!env.PICS || !/^[0-9a-f]{32}\.(png|jpg)$/.test(id)) return new Response("Not here.", { status: 404 });
  const { value, metadata } = await env.PICS.getWithMetadata(id, { type: "arrayBuffer" });
  if (!value) return new Response("Not here.", { status: 404 });
  return new Response(value, {
    headers: {
      "content-type": (metadata && metadata.type) || TYPES[id.split(".")[1]],
      "cache-control": "public, max-age=31536000, immutable",
      "x-content-type-options": "nosniff",
      "content-security-policy": "default-src 'none'",
    },
  });
}

const randomId = () => [...crypto.getRandomValues(new Uint8Array(16))].map((b) => b.toString(16).padStart(2, "0")).join("");

// ---------------------------------------------------------------- checking what came in

const raw = (s, max) => (typeof s === "string" ? s.replace(/\r\n/g, "\n").replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, "").trim().slice(0, max) : "");
/** Text that goes on the public issue: no bad language (censor.js). */
const clean = (s, max) => censor(raw(s, max));

/** Discord names: today's (2-32 of a-z 0-9 _ .) or the old Name#1234. Empty = none left. */
export function discordName(s) {
  const d = raw(s, 40);
  if (!d) return { ok: true, value: "" };
  const v = d.replace(/^@/, "");
  if (/^[a-z0-9_.]{2,32}$/i.test(v) && !/\.\./.test(v)) return { ok: true, value: v.toLowerCase() };
  if (/^[^@#:`]{2,32}#\d{4}$/.test(v)) return { ok: true, value: v };
  return { ok: false };
}

export function validate(x) {
  if (!x || typeof x !== "object") return { error: "That wasn't a report." };
  const kind = KINDS.includes(x.kind) ? x.kind : "Bug", area = AREAS.includes(x.area) ? x.area : "Something else";
  const title = clean(x.title, LIMITS.title), what = clean(x.what, LIMITS.what);
  if (title.length < 4) return { error: "A title, please (a few words)." };
  if (what.length < 4) return { error: "Say what happened, please." };
  const d = discordName(x.discord);
  if (!d.ok) return { error: "That Discord name doesn't look right - it's the name under your profile picture, like coolname or cool.name." };
  const video = x.video === true;
  if (video && !d.value) return { error: "Add your Discord name so the developer can ask you for the video." };
  const images = [];
  for (const img of Array.isArray(x.images) ? x.images.slice(0, LIMITS.images + 1) : []) {
    if (images.length >= LIMITS.images) return { error: `${LIMITS.images} pictures at most.` };
    const pic = picture(img);
    if (!pic) return { error: "A picture couldn't be read (PNG or JPEG screenshots up to 3 MB each - no videos or GIFs)." };
    images.push(pic);
  }
  return {
    kind, area, title, what, discord: d.value, video, images,
    steps: clean(x.steps, LIMITS.steps), details: clean(x.details, LIMITS.details),
    version: clean(x.version, LIMITS.version).replace(/[^0-9A-Za-z.+-]/g, ""), system: clean(x.system, LIMITS.system),
  };
}

/** A picture: base64 of a real PNG or JPEG (checked by its first bytes, hidden details taken out), within the size limit.
 *  GIFs and videos aren't taken - recordings go to the developer on Discord. */
export function picture(img) {
  const b64 = typeof img === "string" ? img : img && typeof img.data === "string" ? img.data : null;
  if (!b64 || !/^[A-Za-z0-9+/=\s]+$/.test(b64)) return null;
  const compact = b64.replace(/\s/g, "");
  if (compact.length * 0.75 > LIMITS.imageBytes) return null;
  let b;
  try { b = fromBase64(compact); } catch { return null; }
  const ext = b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47 ? "png" : b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff ? "jpg" : null;
  if (!ext) return null;
  const bytes = stripDetails(b, ext);
  return bytes ? { ext, bytes } : null;
}

// ---------------------------------------------------------------- what goes where

/** Reports can't ping anyone or close issues ("@name", "fixes #12" become plain text). */
export const quiet = (s) => s.replace(/@(?=[A-Za-z0-9-])/g, "@​").replace(/\b(close[sd]?|fix(e[sd])?|resolve[sd]?)(\s+#\d)/gi, "$1​$2");

export function publicIssue(r, shown = [], leftOut = 0) {
  const p = [];
  p.push(`### Kind\n${r.kind}`, `### Which part?\n${r.area}`, `### What happened?\n${quiet(r.what)}`);
  if (r.steps) p.push(`### How can we make it happen?\n${quiet(r.steps)}`);
  if (shown.length) p.push(`### Screenshots\n${shown.map((u, i) => `![screenshot ${i + 1}](${u})`).join("\n")}`);
  if (r.video) p.push("### Video proof\nYes - the reporter has a recording of it. The developer will ask them for it.");
  p.push(`### StreamLite version\n${r.version || "unknown"}`, `### System\n${quiet(r.system) || "unknown"}`);
  if (r.details) p.push(`### Details from StreamLite\n\`\`\`\n${r.details.replace(/```/g, "'''")}\n\`\`\``);
  const notes = [];
  if (leftOut) notes.push(`${leftOut} picture${leftOut === 1 ? " was" : "s were"} left out (only family-friendly screenshots of an app or an error are shown)`);
  if (r.discord) notes.push("the reporter left a contact for the developer (private)");
  if (notes.length) p.push(`_${notes.join("; ")}._`);
  p.push("<sub>Sent from StreamLite's Report a bug - no account needed.</sub>");
  const prefix = r.kind === KINDS[4] ? "[Idea] " : "[Bug] ";
  return { title: prefix + quiet(r.title), body: p.join("\n\n"), label: r.kind === KINDS[4] ? "enhancement" : "bug" };
}

export function privateNote(r, pub) {
  const p = [`Public report: ${pub.html_url}`, `**Discord:** \`${r.discord}\``];
  if (r.video) p.push("**Video proof:** yes - ask them for it on Discord.");
  return p.join("\n\n");
}

// ---------------------------------------------------------------- spam limits

async function overLimit(env, who) {
  if (!env.RATE) return null;
  const now = new Date(), today = now.toISOString().slice(0, 10);
  const hour = `h:${who}:${now.toISOString().slice(0, 13)}`, day = `d:${who}:${today}`, all = `all:${today}`;
  const [h, d, a] = (await Promise.all([env.RATE.get(hour), env.RATE.get(day), env.RATE.get(all)])).map((v) => Number(v || 0));
  if (h >= LIMITS.perHour) return "That's a lot of reports in an hour - please try again later.";
  if (d >= LIMITS.perDay) return "That's a lot of reports today - please try again tomorrow.";
  if (a >= LIMITS.allDay) return "StreamLite got a lot of reports today - please try again tomorrow.";
  await Promise.all([
    env.RATE.put(hour, String(h + 1), { expirationTtl: 3700 }),
    env.RATE.put(day, String(d + 1), { expirationTtl: 90000 }),
    env.RATE.put(all, String(a + 1), { expirationTtl: 90000 }),
  ]);
  return null;
}

// ---------------------------------------------------------------- GitHub App sign-in

const b64url = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const b64urlText = (s) => b64url(new TextEncoder().encode(s));

async function appJwt(env) {
  const pem = env.APP_PRIVATE_KEY.replace(/-----[^-]+-----/g, "").replace(/\s/g, "");
  const der = Uint8Array.from(atob(pem), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey("pkcs8", der, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const now = Math.floor(Date.now() / 1000);
  const head = b64urlText(JSON.stringify({ alg: "RS256", typ: "JWT" })), body = b64urlText(JSON.stringify({ iat: now - 60, exp: now + 540, iss: env.APP_ID }));
  const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(`${head}.${body}`));
  return `${head}.${body}.${b64url(sig)}`;
}

/** Tokens limited to one repository and only the permissions this step needs. */
async function appToken(env) {
  const jwt = await appJwt(env);
  const cache = {};
  return {
    async token(repo, permissions) {
      const inst = cache[repo] || (cache[repo] = await gh(env, jwt, "GET", `/repos/${repo}/installation`));
      const t = await gh(env, jwt, "POST", `/app/installations/${inst.id}/access_tokens`, { repositories: [repo.split("/")[1]], permissions });
      return t.token;
    },
  };
}

async function gh(env, token, method, path, body) {
  const res = await fetch(`https://api.github.com${path}`, {
    method,
    headers: { authorization: `Bearer ${await token}`, accept: "application/vnd.github+json", "user-agent": "StreamLite-Reports", "x-github-api-version": "2022-11-28", ...(body ? { "content-type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`GitHub ${res.status} on ${method} ${path.replace(/\/issues.*/, "/issues")}`);
  return res.status === 204 ? {} : res.json();
}
