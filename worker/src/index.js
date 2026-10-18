// CLIo speech-to-text Worker: forwards short audio clips to Workers AI Whisper and returns { "text": "..." }.
//
// Request (what CLIo sends): POST /  or  POST /v1/audio/transcriptions
//   Header  X-Clio-Install: <random per-install UUID>   (used only for rate limiting)
//   Body    multipart/form-data with  file (16 kHz mono WAV, <= 2 MB),  optional language, prompt
//           (a raw audio/wav body is accepted too)
// Response: 200 { "text": "..." }   or   { "error": "..." } with 4xx/5xx.
//
// This is rate limiting, not authentication: the install id is self-issued, so it only slows down
// casual abuse. See README.md for what that does and does not protect.

const MODEL = "@cf/openai/whisper-large-v3-turbo";
const MAX_BYTES = 2 * 1024 * 1024; // ~65 s of 16 kHz mono 16-bit audio; CLIo sends clips of a few seconds
const MIN_BYTES = 1000;
const ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PATHS = new Set(["/", "/v1/audio/transcriptions"]);

export default {
  async fetch(request, env) {
    return handle(request, env);
  },
};

export async function handle(request, env) {
  const url = new URL(request.url);

  if (request.method === "GET" && url.pathname === "/") {
    return json({ ok: true, service: "clio-stt" });
  }
  if (!PATHS.has(url.pathname)) return json({ error: "not found" }, 404);
  if (request.method !== "POST") return json({ error: "POST only" }, 405, { Allow: "POST" });

  const installId = request.headers.get("X-Clio-Install") ?? "";
  if (!ID_RE.test(installId)) return json({ error: "missing or invalid X-Clio-Install header" }, 400);

  const declared = Number(request.headers.get("Content-Length") ?? "0");
  if (declared > MAX_BYTES + 4096) return json({ error: "audio too large" }, 413);

  // Rate limits (bindings are optional so local dev and tests work without them).
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  if (!(await allowed(env.IP_LIMITER, `ip:${ip}`)) || !(await allowed(env.INSTALL_LIMITER, `id:${installId.toLowerCase()}`))) {
    return json({ error: "rate limit exceeded, slow down" }, 429, { "Retry-After": "10" });
  }

  let audio;
  let language = "en";
  let prompt = "";
  try {
    const type = (request.headers.get("Content-Type") ?? "").toLowerCase();
    if (type.startsWith("multipart/form-data")) {
      const form = await request.formData();
      const file = form.get("file");
      if (!file || typeof file === "string") return json({ error: "missing file field" }, 400);
      audio = new Uint8Array(await file.arrayBuffer());
      const lang = form.get("language");
      if (typeof lang === "string" && /^[a-z]{2,3}$/i.test(lang)) language = lang.toLowerCase();
      const p = form.get("prompt");
      if (typeof p === "string") prompt = p.trim().slice(0, 600);
    } else if (type.startsWith("audio/")) {
      audio = new Uint8Array(await request.arrayBuffer());
    } else {
      return json({ error: "send multipart/form-data with a file field, or an audio/wav body" }, 415);
    }
  } catch {
    return json({ error: "could not read request body" }, 400);
  }

  if (audio.byteLength > MAX_BYTES) return json({ error: "audio too large" }, 413);
  if (audio.byteLength < MIN_BYTES) return json({ error: "audio too short" }, 400);
  if (!isWav(audio)) return json({ error: "expected WAV audio" }, 415);

  try {
    const input = {
      audio: toBase64(audio),
      language,
      condition_on_previous_text: false, // each clip is independent; avoids repetition loops
    };
    if (prompt) input.initial_prompt = prompt;

    const result = await env.AI.run(MODEL, input);
    const text = (result?.text ?? result?.transcription_info?.text ?? "").toString().trim();
    return json({ text });
  } catch (err) {
    // Log the failure class only; never audio, ids or prompts.
    console.error("workers-ai failure:", err?.name ?? "error");
    return json({ error: "transcription failed" }, 502);
  }
}

async function allowed(limiter, key) {
  if (!limiter) return true;
  const { success } = await limiter.limit({ key });
  return success;
}

function isWav(bytes) {
  // "RIFF" .... "WAVE"
  return (
    bytes.byteLength >= 12 &&
    bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46 &&
    bytes[8] === 0x57 && bytes[9] === 0x41 && bytes[10] === 0x56 && bytes[11] === 0x45
  );
}

function toBase64(bytes) {
  let binary = "";
  const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
  }
  return btoa(binary);
}

function json(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers },
  });
}
