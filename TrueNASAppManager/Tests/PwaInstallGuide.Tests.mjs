import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const installGuide = require("../AppManagerBlazor/wwwroot/pwa-install-guide.js");

test("the web app manifest includes credentials for authenticated reverse proxies", async () => {
    const appMarkup = await readFile(new URL("../AppManagerBlazor/Components/App.razor", import.meta.url), "utf8");

    assert.match(appMarkup, /<link\s+rel="manifest"[^>]*crossorigin="use-credentials"[^>]*>/i);
});

test("classifyPlatform recognizes Samsung Internet on a Galaxy S24", () => {
    const userAgent = "Mozilla/5.0 (Linux; Android 16; SM-S921U) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/28.0 Chrome/130.0.0.0 Mobile Safari/537.36";

    const platform = installGuide.classifyPlatform(userAgent);

    assert.equal(platform, installGuide.Platform.SamsungInternet);
});

test("classifyPlatform keeps an Android embedded browser out of the install flow", () => {
    const userAgent = "Mozilla/5.0 (Linux; Android 15; SM-S921U Build/AP3A; wv) AppleWebKit/537.36 Version/4.0 Chrome/129.0.0.0 Mobile Safari/537.36 GSA/15.34";

    const platform = installGuide.classifyPlatform(userAgent);

    assert.equal(platform, installGuide.Platform.AndroidInApp);
});

test("getGuidance explains the HTTPS requirement before browser-specific steps", () => {
    const guidance = installGuide.getGuidance(installGuide.Platform.SamsungInternet, false);

    assert.equal(guidance.title, "Open the HTTPS App Manager address");
    assert.match(guidance.message, /blocks app installation from plain HTTP/i);
    assert.match(guidance.steps.join(" "), /trusted HTTPS reverse-proxy address/i);
});

test("getGuidance gives Samsung Internet menu and app-screen directions", () => {
    const guidance = installGuide.getGuidance(installGuide.Platform.SamsungInternet, true);

    assert.equal(guidance.title, "Install on your Galaxy");
    assert.match(guidance.steps.join(" "), /Add page to/i);
    assert.match(guidance.steps.join(" "), /Apps screen/i);
});

test("getGuidance gives Chrome-compatible Android directions", () => {
    const guidance = installGuide.getGuidance(installGuide.Platform.AndroidChrome, true);

    assert.equal(guidance.title, "Install on Android");
    assert.match(guidance.steps.join(" "), /Install app or Add to Home screen/i);
});
