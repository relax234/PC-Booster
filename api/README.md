# PC Booster AI API

Minimal Node.js API for the website's AI assistant, powered by Groq's free API tier.

## Endpoints
- `GET /health` — service health and whether a Groq key is configured.
- `POST /api/assistant` — sends a bounded user message to Groq's OpenAI-compatible Responses API.

## Render setup
- Build command: `cd api && npm install`
- Start command: `cd api && npm start`
- Set `FRONTEND_ORIGIN=https://pc-booster-ai.onrender.com`
- Set `GROQ_API_KEY` in Render Environment (never in GitHub or browser code).
- Optional: `GROQ_MODEL=openai/gpt-oss-120b`

Create a free key at https://console.groq.com/keys. Free-tier limits apply and can change; this is not unlimited service.

The service deliberately does not expose system-control tools. The assistant is advisory until a separately installed Windows Helper is paired and an allowlisted action is explicitly approved by the user.
