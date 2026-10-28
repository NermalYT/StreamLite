// node --test test/   (no Cloudflare needed: the parts that decide what a report becomes)
import { test } from "node:test";
import assert from "node:assert/strict";
import { censor } from "../src/censor.js";
import { validate, publicIssue, privateNote, discordName, picture, quiet } from "../src/index.js";
import { verdict, checkPicture, stripDetails } from "../src/moderate.js";

const r13 = (s) => s.replace(/[a-z]/g, (c) => String.fromCharCode(((c.charCodeAt(0) - 97 + 13) % 26) + 97));
const bad = (w) => r13(w);   // the test's words, stored like censor.js stores them

test("curse words and slurs become ****, disguised ones too", () => {
  for (const w of ["shpx", "fuvg", "ovgpu", "phag", "nffubyr", "avttre", "snttbg", "ergneq", "zbgureshpxre", "ohyyfuvg", "fuvgurnq", "shpxvat"]) {
    assert.equal(censor(`this ${bad(w)} thing`), "this **** thing", bad(w));
  }
});

// Bypasses people use (stored ROT13 like the word lists): swapped letters, symbols, vowels left out, spaced apart...
const BYPASSES = ["s*px", "fu1g", "shhhhpx", "s.h.p.x", "$uvg", "o1gpu", "shpxvat", "ivttre", "i1tt3e", "a1tt@", "alttn", "a v t t r e", "s h p x", "s-h-p-x",
  "spx", "spxvat", "cuhpx", "shd", "n$$ubyr", "o!gpu", "fu!g", "jgs", "fgsh", "ovngpu", "sipx", "shkx", "shpp", "fu*g", "p*ag", "q1px", "c0ea",
  "a!ttn", "snt", "s@ttbg", "e3gneq", "ge@aal", "ohyyfu1g", "zbguresipxre", "fu1gurnq", "shpxshpxshpx"];

test("every bypass is caught", () => {
  for (const w of BYPASSES) {
    const s = r13(w);
    assert.equal(censor(`ok ${s} ok`), "ok **** ok", w);
    assert.equal(censor(`ok ${s.toUpperCase()} ok`), "ok **** ok", w + " in capitals");
  }
});

test("Unicode tricks: look-alike letters, fancy and fullwidth letters, accents, invisible characters", () => {
  for (const w of ["shpx", "avttn", "fuvg", "ovgpu"]) {
    const s = r13(w);
    const cyr = s.replace(/a/g, "а").replace(/e/g, "е").replace(/o/g, "о").replace(/c/g, "с").replace(/i/g, "і");   // Cyrillic
    const full = [...s].map((c) => String.fromCharCode(c.charCodeAt(0) + 0xfee0)).join("");                          // ｆｕｌｌｗｉｄｔｈ
    const bold = [...s].map((c) => String.fromCodePoint(0x1d41a + c.charCodeAt(0) - 97)).join("");                  // 𝐛𝐨𝐥𝐝
    const zw = [...s].join("​");                                                                                 // zero-width spaces
    const acc = s.replace(/u/g, "ü").replace(/i/g, "í").replace(/a/g, "á");                                           // accents
    for (const v of [cyr, full, bold, zw, acc]) assert.equal(censor(`x ${v} y`), "x **** y", `${w}: ${[...v].length} chars`);
  }
});

test("mild words a family film says stay readable", () => {
  for (const s of ["damn it", "this is crap", "what the hell", "heck", "pissed off", "darn", "that sucks", "oh my god", "freaking", "frick", "jeez"]) assert.equal(censor(s), s, s);
});

test("harmless words that contain a bad one are left alone", () => {
  for (const s of ["Scunthorpe", "raccoon", "assess the class", "cockpit", "hello", "shell", "assume", "Dickens", "classic", "grass", "therapist", "cucumber",
    "skyscraper", "Matsushita", "a niggling bug", "niggles", "sniggering", "Assassin", "passive", "Hancock", "scrap", "therapists", "bigger", "digger",
    "trigger", "jigger", "Niger", "Nigeria", "count", "cnt", "assist", "cocktail", "spice", "Viggo", "titles", "analysis", "cumulative", "Pakistan",
    "a b c d", "FAQ", "cumin", "shiitake", "Fagin", "spick and span", "chinking glasses", "horny toad", "press A B C", "3 2 1 go", "Michelle", "hellos", "fuchsia", "Dickinson", "Sussex", "Essex", "class action"])
    assert.equal(censor(s), s, s);
});

test("a report is checked and cleaned", () => {
  assert.match(validate({ title: "x", what: "something" }).error, /title/);
  assert.match(validate({ title: "Mic cuts out", what: "" }).error, /what happened/i);
  const r = validate({ title: `My ${bad("shpxvat")} mic`, what: `It's ${bad("fuvg")}`, kind: "nonsense", area: "nonsense", version: "4.1.2<script>", discord: "Cool.Name" });
  assert.equal(r.title, "My **** mic");
  assert.equal(r.what, "It's ****");
  assert.equal(r.kind, "Bug"); assert.equal(r.area, "Something else");
  assert.equal(r.version, "4.1.2script");
  assert.equal(r.discord, "cool.name");
});

test("Discord names: today's and the old #1234 kind; nothing else", () => {
  assert.deepEqual(discordName(""), { ok: true, value: "" });
  assert.equal(discordName("@some_user.1").value, "some_user.1");
  assert.equal(discordName("Old Name#1234").value, "Old Name#1234");
  assert.equal(discordName("a").ok, false);
  assert.equal(discordName("bad..dots").ok, false);
  assert.equal(discordName("https://evil.example/x").ok, false);
});

// A tiny real PNG: signature, IHDR, a tEXt chunk with hidden details, IDAT, IEND (CRCs aren't checked here).
const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); return Buffer.concat([len, Buffer.from(type), data, Buffer.alloc(4)]); };
const PNG = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", Buffer.alloc(13)), chunk("tEXt", Buffer.from("Author\0C:\\Users\\RealName")), chunk("IDAT", Buffer.from([1, 2, 3])), chunk("IEND", Buffer.alloc(0))]);
const JPG = Buffer.concat([Buffer.from([0xff, 0xd8]), Buffer.from([0xff, 0xe0, 0, 4, 0x4a, 0x46]), Buffer.from([0xff, 0xe1, 0, 8]), Buffer.from("Exif\0G"), Buffer.from([0xff, 0xfe, 0, 5]), Buffer.from("hi!"), Buffer.from([0xff, 0xda, 0, 2, 9, 9, 0xff, 0xd9])]);

test("the Discord name never reaches the public issue; checked screenshots and video proof do", () => {
  const r = validate({ title: "Crash on start", what: "It closes", discord: "secret.person", video: true, images: [PNG.toString("base64")] });
  const pub = publicIssue(r, ["https://w.example/p/0123456789abcdef0123456789abcdef.png"], 1);
  assert.ok(!pub.body.includes("secret.person") && !pub.title.includes("secret.person"));
  assert.match(pub.body, /left a contact for the developer \(private\)/);
  assert.match(pub.body, /### Screenshots\n!\[screenshot 1\]\(https:\/\/w\.example\/p\//);
  assert.match(pub.body, /### Video proof\nYes/);
  assert.match(pub.body, /1 picture was left out/);
  const note = privateNote(r, { html_url: "https://github.com/NermalYT/StreamLite/issues/7" });
  assert.match(note, /secret\.person/);
  assert.match(note, /issues\/7/);
  assert.match(note, /Video proof/);
});

test("video proof needs a Discord name, so the developer can ask for it", () => {
  assert.match(validate({ title: "Robot voice", what: "Sounds bad", video: true }).error, /Discord name/);
  assert.equal(validate({ title: "Robot voice", what: "Sounds bad", video: "yes please" }).video, false);
});

test("hidden details are taken out of pictures", () => {
  const p = picture(PNG.toString("base64"));
  assert.equal(p.ext, "png");
  assert.ok(!Buffer.from(p.bytes).includes("RealName") && Buffer.from(p.bytes).includes("IDAT") && Buffer.from(p.bytes).includes("IEND"));
  const j = picture(JPG.toString("base64"));
  assert.equal(j.ext, "jpg");
  const jb = Buffer.from(j.bytes);
  assert.ok(!jb.includes("Exif") && !jb.includes("hi!") && jb.includes(Buffer.from([0x4a, 0x46])) && jb.includes(Buffer.from([0xff, 0xda])));
  assert.equal(stripDetails(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 99]), "png"), null);   // cut short
});

test("a screenshot is shown only when the check clearly says it's a safe screenshot", () => {
  assert.deepEqual(verdict('{"screenshot": true, "safe": true, "text": "StreamLite - Microphone"}'), { ok: true });
  assert.deepEqual(verdict({ screenshot: true, safe: true, text: "" }), { ok: true });
  assert.equal(verdict('Sure! {"screenshot": false, "safe": true}').why, "not a screenshot");
  assert.equal(verdict('{"screenshot": true, "safe": false}').why, "not family friendly");
  assert.equal(verdict(`{"screenshot": true, "safe": true, "text": "error ${bad("shpx")} you"}`).why, "not family friendly");
  assert.equal(verdict('{"screenshot": "true", "safe": true}').why, "not a screenshot");
  for (const x of ["", "no idea", "{broken", null, undefined, 42]) assert.equal(verdict(x).why, "unchecked");
});

test("no picture is shown without the check (no AI = left out)", async () => {
  assert.equal((await checkPicture({}, PNG.toString("base64"), "png")).ok, false);
  const failing = { AI: { run: async () => { throw new Error("daily allowance used up"); } } };
  assert.equal((await checkPicture(failing, PNG.toString("base64"), "png")).why, "unchecked");
  let asked;
  const fine = { AI: { run: async (m, input) => { asked = input; return { response: '{"screenshot":true,"safe":true,"text":"Settings"}' }; } } };
  assert.equal((await checkPicture(fine, "AAAA", "jpg")).ok, true);
  assert.match(asked.messages[1].content[1].image_url.url, /^data:image\/jpeg;base64,AAAA$/);
});

test("reports can't ping people or close issues", () => {
  assert.equal(quiet("@octocat look"), "@​octocat look");
  assert.ok(!/fixes #12/.test(quiet("this fixes #12")));
  const p = publicIssue(validate({ title: "@everyone hi", what: "closes #3 please" }));
  assert.ok(!p.title.includes("@everyone") && !p.body.includes("closes #3"));
});

test("pictures: real PNG / JPEG only - no GIFs, videos or anything else", () => {
  assert.equal(picture(JPG.toString("base64")).ext, "jpg");
  assert.equal(picture(Buffer.from("GIF89a....").toString("base64")), null);
  assert.equal(picture(Buffer.from([0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d]).toString("base64")), null);   // an MP4
  assert.equal(picture(Buffer.from([0x1a, 0x45, 0xdf, 0xa3, 0, 0]).toString("base64")), null);                                     // a WebM
  assert.equal(picture(Buffer.from("<svg onload=x>").toString("base64")), null);
  assert.equal(picture("not base64 !!"), null);
  assert.match(validate({ title: "Five pictures", what: "too many", images: Array(5).fill(PNG.toString("base64")) }).error, /at most/);
  assert.match(validate({ title: "A GIF", what: "animated", images: [Buffer.from("GIF89a").toString("base64")] }).error, /no videos or GIFs/);
});

test("ideas are labelled as ideas", () => {
  const p = publicIssue(validate({ title: "Dark mode", what: "Please add it", kind: "Idea to improve StreamLite" }));
  assert.equal(p.label, "enhancement"); assert.match(p.title, /^\[Idea\]/);
});
