import http from "node:http";

const PORT = Number(process.env.PORT || 3000);
const FRONTEND_ORIGIN = process.env.FRONTEND_ORIGIN || "https://pc-booster-ai.onrender.com";
const MODEL = process.env.GROQ_MODEL || "openai/gpt-oss-120b";
const WINDOW_MS = 60_000;
const MAX_REQUESTS = 12;
const MAX_BODY = 8_000;
const requests = new Map();

function send(res, status, payload) {
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
  res.end(JSON.stringify(payload));
}
function allowedOrigin(origin) {
  return !origin || origin === FRONTEND_ORIGIN || /^https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin);
}
function rateLimited(key) {
  const now = Date.now();
  const recent = (requests.get(key) || []).filter(t => now - t < WINDOW_MS);
  if (recent.length >= MAX_REQUESTS) return true;
  recent.push(now);
  requests.set(key, recent);
  return false;
}
function getText(data) {
  if (typeof data.output_text === "string") return data.output_text;
  for (const item of data.output || []) {
    if (item.type === "message") {
      for (const part of item.content || []) if (part.type === "output_text" && part.text) return part.text;
    }
  }
  return "";
}
const instructions = `Ты — PC Booster AI, помощник по обслуживанию Windows 10/11. Отвечай по-русски, ясно и практично. Приоритет — безопасность, стабильность и прозрачность. Не обещай гарантированный прирост FPS. Не советуй отключать Microsoft Defender, Firewall, Windows Update, UAC, системные службы безопасности или удалять неизвестные системные файлы. Разделяй диагностику и изменение системы. Перед рискованными действиями требуй явного подтверждения и объясняй последствия. Сейчас ты только консультируешь: не утверждай, что уже просканировал или изменил компьютер.`;

const server = http.createServer(async (req, res) => {
  const origin = req.headers.origin || "";
  if (!allowedOrigin(origin)) return send(res, 403, { error: "Origin not allowed" });
  if (origin) {
    res.setHeader("access-control-allow-origin", origin);
    res.setHeader("vary", "Origin");
    res.setHeader("access-control-allow-headers", "content-type");
    res.setHeader("access-control-allow-methods", "GET, POST, OPTIONS");
  }
  if (req.method === "OPTIONS") { res.writeHead(204); return res.end(); }
  const url = new URL(req.url, "http://localhost");
  if (req.method === "GET" && url.pathname === "/health") {
    return send(res, 200, { ok: true, service: "pc-booster-ai-api", provider: "groq", aiConfigured: Boolean(process.env.GROQ_API_KEY), model: MODEL });
  }
  if (req.method !== "POST" || url.pathname !== "/api/assistant") return send(res, 404, { error: "Not found" });
  const ip = (req.headers["x-forwarded-for"] || "").toString().split(",")[0].trim() || req.socket.remoteAddress || "unknown";
  if (rateLimited(ip)) return send(res, 429, { error: "Слишком много запросов. Попробуй через минуту." });
  let raw = "";
  try {
    for await (const chunk of req) {
      raw += chunk;
      if (raw.length > MAX_BODY) return send(res, 413, { error: "Сообщение слишком большое." });
    }
    const body = JSON.parse(raw || "{}");
    const message = typeof body.message === "string" ? body.message.trim() : "";
    if (!message || message.length > 2000) return send(res, 400, { error: "Напиши сообщение до 2000 символов." });
    if (!process.env.GROQ_API_KEY) return send(res, 503, { error: "Бесплатный AI ещё не подключён: добавь GROQ_API_KEY в настройках сервера Render." });
    const upstream = await fetch("https://api.groq.com/openai/v1/responses", {
      method: "POST",
      headers: { "authorization": `Bearer ${process.env.GROQ_API_KEY}`, "content-type": "application/json" },
      body: JSON.stringify({ model: MODEL, instructions, input: message, max_output_tokens: 700, store: false }),
      signal: AbortSignal.timeout(30_000)
    });
    const data = await upstream.json().catch(() => ({}));
    if (!upstream.ok) {
      console.error("Groq request failed:", upstream.status, data?.error?.type || data?.error?.code || "upstream_error");
      return send(res, 502, { error: "Бесплатный AI временно недоступен или достиг лимита. Попробуй немного позже." });
    }
    const answer = getText(data);
    if (!answer) return send(res, 502, { error: "AI вернул пустой ответ. Попробуй ещё раз." });
    return send(res, 200, { answer });
  } catch (error) {
    console.error("Request failed:", error?.name || "unknown");
    return send(res, 500, { error: "Внутренняя ошибка сервера." });
  }
});
server.listen(PORT, "0.0.0.0", () => console.log(`PC Booster API listening on ${PORT}`));
