import test from "node:test";
import assert from "node:assert/strict";
import { handle } from "../src/index.js";

const ID = "123e4567-e89b-42d3-a456-426614174000";

function wav(bytes = 4000) {
  const data = new Uint8Array(bytes);
  data.set([0x52, 0x49, 0x46, 0x46], 0); // RIFF
  data.set([0x57, 0x41, 0x56, 0x45], 8); // WAVE
  return data;
}

function upload({ id = ID, audio = wav(), language = "en", prompt = "Names: Docklite", method = "POST", path = "/" } = {}) {
  const form = new FormData();
  form.set("file", new Blob([audio], { type: "audio/wav" }), "audio.wav");
  form.set("language", language);
  form.set("prompt", prompt);
  const headers = {};
  if (id) headers["X-Clio-Install"] = id;
  return new Request(`https://stt.example.workers.dev${path}`, { method, body: method === "POST" ? form : undefined, headers });
}

function env({ text = "hello world", limiter } = {}) {
  const calls = [];
  return {
    calls,
    AI: { run: async (model, input) => { calls.push({ model, input }); return { text }; } },
    INSTALL_LIMITER: limiter,
  };
}

test("transcribes a wav upload and returns { text }", async () => {
  const e = env();
  const res = await handle(upload(), e);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { text: "hello world" });
  assert.equal(e.calls[0].model, "@cf/openai/whisper-large-v3-turbo");
  assert.equal(typeof e.calls[0].input.audio, "string"); // base64
  assert.equal(e.calls[0].input.language, "en");
  assert.equal(e.calls[0].input.initial_prompt, "Names: Docklite");
  assert.equal(Buffer.from(e.calls[0].input.audio, "base64").byteLength, 4000);
});

test("accepts the /v1/audio/transcriptions path", async () => {
  const res = await handle(upload({ path: "/v1/audio/transcriptions" }), env());
  assert.equal(res.status, 200);
});

test("rejects a missing or malformed install id", async () => {
  assert.equal((await handle(upload({ id: "" }), env())).status, 400);
  assert.equal((await handle(upload({ id: "not-a-uuid" }), env())).status, 400);
});

test("rejects non-wav audio and non-multipart bodies", async () => {
  const bad = new Uint8Array(4000); // no RIFF header
  assert.equal((await handle(upload({ audio: bad }), env())).status, 415);
  const req = new Request("https://x.workers.dev/", { method: "POST", body: "hi", headers: { "X-Clio-Install": ID, "Content-Type": "text/plain" } });
  assert.equal((await handle(req, env())).status, 415);
});

test("rejects oversized and tiny audio", async () => {
  assert.equal((await handle(upload({ audio: wav(3 * 1024 * 1024) }), env())).status, 413);
  assert.equal((await handle(upload({ audio: wav(100) }), env())).status, 400);
});

test("enforces the rate limiter", async () => {
  const limiter = { limit: async () => ({ success: false }) };
  const res = await handle(upload(), env({ limiter }));
  assert.equal(res.status, 429);
});

test("only POST is allowed; GET / is a health check", async () => {
  const get = new Request("https://x.workers.dev/", { method: "GET" });
  assert.equal((await handle(get, env())).status, 200);
  const put = new Request("https://x.workers.dev/", { method: "PUT", headers: { "X-Clio-Install": ID } });
  assert.equal((await handle(put, env())).status, 405);
});

test("maps AI failures to 502 without leaking details", async () => {
  const e = { AI: { run: async () => { throw new Error("secret detail"); } } };
  const res = await handle(upload(), e);
  assert.equal(res.status, 502);
  assert.deepEqual(await res.json(), { error: "transcription failed" });
});
