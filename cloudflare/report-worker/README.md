# StreamLite's bug reports

StreamLite's **Report a bug** sends here, and this Cloudflare Worker posts the report on
[StreamLite's issue list](https://github.com/NermalYT/StreamLite/issues) as the "StreamLite Reports" GitHub App.
Nobody needs an account.

- **Bad language** in the title and text is starred out (`src/censor.js`). Mild words like damn or crap stay.
- **Screenshots** are checked by a vision model on Workers AI (`src/moderate.js`). Only family-friendly screenshots of an
  app or an error are kept, with hidden file details removed, and shown on the report from `/p/<id>`. Anything else,
  or anything that couldn't be checked, is left out.
- **Videos and GIFs** aren't taken. "I have a video" shows as *Video proof: yes* on the report.
- **The Discord name** goes only to the private `streamlite-reports` repository, never to the public issue.
- **Spam limits:** 5 reports an hour and 15 a day per sender, 100 a day in all.
- **Nothing** a report contains is logged.

It all runs on Cloudflare's free plan with no card: Workers, KV and the Workers AI free allowance. Past a free daily
allowance things stop for the day; nothing is ever billed.

## Setting it up

1. Create the GitHub App (Issues: read and write, Metadata: read, no webhook). Install it on `StreamLite` and
   `streamlite-reports`.
2. `npm install`, then `npx wrangler login`.
3. `npx wrangler kv namespace create RATE` and `npx wrangler kv namespace create PICS`, then put the two ids in
   `wrangler.toml`.
4. `npx wrangler secret put APP_ID`, then `npx wrangler secret put APP_PRIVATE_KEY` (the App's key as PKCS#8:
   `openssl pkcs8 -topk8 -nocrypt -in key.pem`).
5. `npx wrangler deploy`. Put the URL it prints, plus `/report`, in `src/BugReports.cs` (`Service`).

Tests: `npm test`.

To take a screenshot off a report, delete its key: `npx wrangler kv key delete --binding PICS <id>.png --remote`.
