import http from "node:http";

const PORT = Number(process.env.PORT || 3000);
const FRONTEND_ORIGIN = process.env.FRONTEND_ORIGIN || "https://pc-booster-ai.onrender.com";
const MODEL = process.env.GROQ_MODEL || "openai/gpt-oss-120b";
const WINDOW_MS = 60_000;
const MAX_REQUESTS = 12;
const MAX_BODY = 24_000;
const requests = new Map();

function send(res, status, payload) {
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
  res.end(JSON.stringify(payload));
}
function allowedOrigin(origin) {
  if (!origin || origin === FRONTEND_ORIGIN) return true;
  try {
    const parsed = new URL(origin);
    return ["http:", "https:"].includes(parsed.protocol) && ["localhost", "127.0.0.1"].includes(parsed.hostname);
  } catch {
    return false;
  }
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
const instructions = `Ты — PC Booster AI, понятный и внимательный помощник по Windows 10/11. Всегда отвечай на русском языке простыми словами, как опытный специалист, который объясняет новичку. Главная цель — чтобы человек сразу понял суть и знал, что делать дальше.\n\nСТИЛЬ ОТВЕТА:\n- Сначала дай короткий прямой ответ по сути (1–2 предложения).\n- Затем, если нужны действия, оформи их нумерованными шагами: одно действие на шаг, точные названия кнопок и разделов Windows.\n- Используй короткие абзацы и понятные формулировки. Объясняй технические термины простыми словами.\n- Не перегружай ответ деталями, не повторяй вопрос и не добавляй общие вступления. Обычно отвечай в 3–8 коротких пунктах; подробно — только когда пользователь просит.\n- Если есть несколько вариантов, сначала предложи самый простой и безопасный. Команды помещай отдельной строкой и поясняй, что они делают.\n- Если данных недостаточно, задай один конкретный уточняющий вопрос вместо догадок. Чётко отделяй известные факты от предположений.\n- В конце при необходимости укажи, что должно измениться после выполнения шагов и что делать, если не помогло.\n\nБЕЗОПАСНОСТЬ И ЧЕСТНОСТЬ:\nПриоритет — безопасность и стабильность. Не обещай гарантированный прирост FPS. Не советуй отключать Microsoft Defender, Firewall, Windows Update, UAC, системные службы безопасности или удалять неизвестные системные файлы. Разделяй диагностику и изменение системы. Перед рискованными действиями требуй явного подтверждения и объясняй последствия. Сейчас ты только консультируешь: не утверждай, что уже просканировал или изменил компьютер. Учитывай предыдущие сообщения диалога, не задавай повторно уже отвеченные вопросы.`;

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
  if (req.method === "GET" && (url.pathname === "/api/updates/latest" || url.pathname === "/api/updates/download")) {
    const token = process.env.GITHUB_TOKEN;
    if (!token) return send(res, 503, { error: "Сервер обновлений ещё не настроен." });
    try {
      const headers = { authorization: `Bearer ${token}`, accept: "application/vnd.github+json", "x-github-api-version": "2022-11-28" };
      const releasesResponse = await fetch("https://api.github.com/repos/relax234/PC-Booster/releases?per_page=30", { headers, signal: AbortSignal.timeout(20000) });
      if (!releasesResponse.ok) throw new Error(`GitHub releases API: ${releasesResponse.status}`);
      const releases = await releasesResponse.json();
      const release = releases.filter(item => !item.draft && !item.prerelease && /^desktop-v\\d+\\.\\d+\\.\\d+$/.test(item.tag_name))
        .sort((a,b) => new Date(b.published_at || b.created_at) - new Date(a.published_at || a.created_at))[0];
      const asset = release?.assets?.find(item => item.name === "PC-Booster.exe");
      if (!release || !asset || !/^sha256:[a-f0-9]{64}$/i.test(asset.digest || "")) throw new Error("Последний desktop-релиз или SHA256 не найден.");
      const version = release.tag_name.replace(/^desktop-v/, "");
      if (url.pathname === "/api/updates/latest") {
        return send(res, 200, { version, tag: release.tag_name, sha256: asset.digest.slice(7).toLowerCase(), size: asset.size, publishedAt: release.published_at });
      }
      const download = await fetch(`https://api.github.com/repos/relax234/PC-Booster/releases/assets/${asset.id}`, {
        headers: { ...headers, accept: "application/octet-stream" }, signal: AbortSignal.timeout(120000)
      });
      if (!download.ok || !download.body) throw new Error(`GitHub asset download: ${download.status}`);
      res.writeHead(200, {
        "content-type": "application/octet-stream",
        "content-disposition": 'attachment; filename="PC-Booster.exe"',
        "content-length": String(asset.size),
        "cache-control": "no-store",
        "x-content-sha256": asset.digest.slice(7).toLowerCase()
      });
      const { Readable } = await import("node:stream");
      Readable.fromWeb(download.body).on("error", error => { console.error("Update stream failed:", error?.name); res.destroy(error); }).pipe(res);
      return;
    } catch (error) {
      console.error("Update service failed:", error?.message || "unknown");
      return send(res, 502, { error: "Не удалось получить обновление. Попробуй позже." });
    }
  }
  if (req.method !== "POST" || url.pathname !== "/api/assistant") return send(res, 404, { error: "Not found" });
  const ip = (req.headers["x-forwarded-for"] || "").toString().split(",")[0].trim() || req.socket.remoteAddress || "unknown";
  if (rateLimited(ip)) return send(res, 429, { error: "Слишком много запросов. Попробуй через минуту." });
  let raw = "";
  try {
    for await (const chunk of req) {
      raw += chunk;
      if (raw.length > MAX_BODY) return send(res, 413, { error: "История чата слишком большая. Начни новый диалог." });
    }
    const body = JSON.parse(raw || "{}");
    const message = typeof body.message === "string" ? body.message.trim() : "";
    if (!message || message.length > 2000) return send(res, 400, { error: "Напиши сообщение до 2000 символов." });
    if (!process.env.GROQ_API_KEY) return send(res, 503, { error: "Бесплатный AI ещё не подключён: добавь GROQ_API_KEY в настройках сервера Render." });
    const supplied = Array.isArray(body.history) ? body.history.slice(-12) : [];
    const history = supplied.map(item => {
      if (!item || !["user", "assistant"].includes(item.role) || typeof item.content !== "string") return null;
      const content = item.content.trim();
      return content && content.length <= 2000 ? { role: item.role, content } : null;
    }).filter(Boolean);
    const input = history.length && history[history.length - 1].role === "user" && history[history.length - 1].content === message
      ? history
      : [...history, { role: "user", content: message }];
    const upstream = await fetch("https://api.groq.com/openai/v1/responses", {
      method: "POST",
      headers: { "authorization": `Bearer ${process.env.GROQ_API_KEY}`, "content-type": "application/json" },
      body: JSON.stringify({ model: MODEL, instructions, input, max_output_tokens: 900, store: false }),
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
