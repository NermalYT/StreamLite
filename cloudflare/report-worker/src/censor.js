// Family-friendly public bug reports (4.1.2): swearing, slurs and sexual words become **** - the mild ones a family film says (damn, crap, hell, heck, piss) stay readable, also when they're
// disguised - swapped letters (v for n, 1 for i, $ for s, ph for f), vowels left out (fck), letters spaced or dotted
// apart (f u c k, f.u.c.k), repeated (fuuuck), look-alike letters from other alphabets (Cyrillic / Greek), fancy
// Unicode letters (bold, fullwidth), accents and invisible characters inside a word. Ordinary words that only
// contain a bad one stay (Scunthorpe, raccoon, assess, cockpit, bigger, trigger, Niger, niggle, Matsushita).
// The words are stored ROT13 so the repository doesn't spell them out.

const rot13 = (s) => s.replace(/[a-z]/g, (c) => String.fromCharCode(((c.charCodeAt(0) - 97 + 13) % 26) + 97));

// Found anywhere inside a word ("motherf...", "...head", "bull...").
const ANYWHERE = ["shpx", "fuvg", "avtt", "ivtt", "snttbg", "zbgureshpx", "phag"].map(rot13);
// Whole words only (with endings): these letters hide in harmless words.
const WORDS = ["spx", "shx", "shd", "shp", "cuhpx", "cuhx", "snjx", "sxvat", "sxva", "sxa", "spxvat", "spxva", "spxa", "fug", "fulg", "fuvgr",
  "ogpu", "ovngpu", "ovbgpu", "ovgpu", "nff", "nffubyr", "qhzonff", "wnpxnff", "onqnff", "onfgneq", "qvpx", "qvpxurnq", "pbpx", "chffl", 
  "fyhg", "juber", "tbqqnza", "gjng", "jnax", "jnaxre", "cevpx", "obyybpxf", "zhgunshpxn", "jgs", "fgsh",
  "tgsb", "yzsnb", "bzst", "cbea", "cbeab", "qvyqb", "phz", "wvmm", "oybjwbo", "unaqwbo", "obbo", "obbof", "gvgf", "gvggvrf", "nany",
  "avttn", "avtn", "avtnf", "avt", "avtf", "avttre", "alttn", "ivttn", "ivttre", "arteb", "snt", "sntf", "ergneq", "ergneqrq", "genaal", "furznyr",
  "fcvp", "puvax", "xvxr", "jrgonpx", "qlxr", "tbbx", "pbba", "ornare", "cnxv", "gbjryurnq", "enturnq", "fnaqavttre", "unwv", "cnwrrg",
  "mvccreurnq", "erqfxva", "fdhnj", "fcnm", "zbatbybvq"].map(rot13);
const ENDINGS = "(?:s|es|ed|er|ers|ing|in|ings|y|ty|ies|z|az|ah|uh|head|heads|hole|holes|face|tard|wad|bag|hat)?";
// Real words those letters hide in (whole tokens; "nigglet" is not one of them).
const HARMLESS = /^(?:niggl(?:e|ed|es|ing|ingly|y)|sniggl(?:e|ed|es|ing)|snigger(?:s|ed|ing)?|matsushita|scunthorpe|viggo|shittim|niger|nigeria|nigerian|nigerians|nigerien|shiitake|cumin|fagin|spick|chinking|scunthorpe)$/i;

// Look-alike letters from other alphabets -> the Latin letter they imitate.
const HOMO = { "а": "a", "е": "e", "о": "o", "р": "p", "с": "c", "у": "y", "х": "x", "к": "k", "і": "i", "ї": "i", "ј": "j", "ѕ": "s", "ԁ": "d", "һ": "h", "ԛ": "q", "ԝ": "w",
  "в": "b", "м": "m", "н": "h", "т": "t", "г": "r", "п": "n", "и": "u", "ь": "b", "α": "a", "β": "b", "ε": "e", "ι": "i", "κ": "k", "ν": "v", "ο": "o", "ρ": "p", "τ": "t", "υ": "u",
  "χ": "x", "ɡ": "g", "ɢ": "g", "ʜ": "h", "ɪ": "i", "ʟ": "l", "ɴ": "n", "ᴏ": "o", "ʀ": "r", "ꜱ": "s", "ᴛ": "t", "ᴜ": "u", "ᴠ": "v", "ᴄ": "c", "ᴋ": "k", "ꜰ": "f", "ı": "i", "ł": "l", "ø": "o", "ß": "ss" };
// Disguises a letter may be written with (after the above): each may repeat, split by one . _ * - ~
const LOOKS = { a: "a4@\\*^", b: "b8", c: "ck\\(q", e: "e3\\*", f: "f", g: "g96", h: "h#", i: "i1!l\\*|y", k: "kcqx", l: "l1|i", n: "n", o: "o0\\*", s: "s5\\$z", t: "t7\\+", u: "uv\\*", z: "z2s" };
const letter = (c) => (c === "f" ? "(?:f|ph)+" : `[${LOOKS[c] || c}]+`);
const spell = (w) => w.split("").map(letter).join("[._*\\-~]?");

const TOKEN = "[a-z0-9@$*!|+#^(]";
const anywhere = new RegExp(`${TOKEN}*(?:${ANYWHERE.map(spell).join("|")})${TOKEN}*`, "gi");
const whole = new RegExp(`(?<![a-z0-9])(?:${WORDS.map(spell).join("|")})${ENDINGS}(?![a-z0-9])`, "gi");

/**
 * The text as the matcher sees it, with where each of its characters came from in the original: invisible characters
 * out, accents and fancy / fullwidth letters plain, look-alikes Latin, lower case.
 */
function plain(text) {
  const out = [], from = [];
  let i = 0;
  for (const ch of text) {
    const at = i; i += ch.length;
    if (/[­͏؜ᅟᅠ឴឵᠋-᠏​-‏‪-‮⁠-⁯ㅤ︀-️﻿]/.test(ch)) continue;
    let n = ch.normalize("NFKD").replace(/[̀-ͯ҃-҉]/g, "").toLowerCase();
    n = [...n].map((c) => HOMO[c] || c).join("");
    for (const c of n) { out.push(c); from.push(at); }
  }
  return { s: out.join(""), from, end: (k) => (k < from.length ? from[k] : text.length) };
}

/** Letters spaced or dotted apart ("f u c k", "n.i.g.g.e.r") joined, so they're seen as the word they spell. */
function joinSpaced(p) {
  const out = [], from = [];
  const runs = /(?<![a-z0-9@$*!|])[a-z0-9@$*!|](?:[\s._\-~,/\\]+[a-z0-9@$*!|](?![a-z0-9@$*!|])){2,}/g;
  let last = 0, m;
  while ((m = runs.exec(p.s))) {
    for (let k = last; k < m.index; k++) { out.push(p.s[k]); from.push(p.from[k]); }
    for (let k = m.index; k < m.index + m[0].length; k++) if (/[a-z0-9@$*!|]/.test(p.s[k])) { out.push(p.s[k]); from.push(p.from[k]); }
    last = m.index + m[0].length;
  }
  for (let k = last; k < p.s.length; k++) { out.push(p.s[k]); from.push(p.from[k]); }
  return { s: out.join(""), from };
}

/** The text with every curse word, slur and sexual word as ****. */
export function censor(text) {
  if (!text) return text;
  const spans = [];
  for (const view of [plain(text), joinSpaced(plain(text))]) {
    const hit = (m, idx) => spans.push([view.from[idx], idx + m.length < view.from.length ? view.from[idx + m.length] : text.length]);
    for (const m of view.s.matchAll(anywhere)) if (/[a-z]/.test(m[0]) && !HARMLESS.test(m[0])) hit(m[0], m.index);
    for (const m of view.s.matchAll(whole)) if (!HARMLESS.test(m[0])) hit(m[0], m.index);
  }
  if (!spans.length) return text;
  // Overlapping hits become one ****; the rest of the text is kept as it was written.
  spans.sort((a, b) => a[0] - b[0]);
  const merged = [];
  for (const sp of spans) {
    const lastSpan = merged[merged.length - 1];
    if (lastSpan && sp[0] <= lastSpan[1]) lastSpan[1] = Math.max(lastSpan[1], sp[1]); else merged.push([...sp]);
  }
  let out = "", at = 0;
  for (const [a, b] of merged) {
    // A hit's end lands on the next kept character: trailing spaces/punctuation from the join stay outside it.
    let end = b; while (end > a && /[\s.,_\-~/\\]/.test(text[end - 1])) end--;
    out += text.slice(at, a) + "****"; at = end;
  }
  return out + text.slice(at);
}
