# PC Booster AI API

Minimal Node.js API for the website's AI assistant.

## Endpoints
- `GET /health` — service health and whether an AI key is configured.
- `POST /api/assistant` — sends a bounded user message to the OpenAI Responses API.

## Render setup
Create a Node web service from this repository:
- Build command: `cd api && npm install`
- Start command: `cd api && npm start`
- Region: Frankfurt
- Set `FRONTEND_ORIGIN=https://pc-booster-ai.onrender.com`
- Set `OPENAI_API_KEY` in Render Environment (never in GitHub or browser code).
- Optional: `OPENAI_MODEL=gpt-6.2-astra`

The service deliberately does not expose system-control tools. The assistant is advisory until a separately installed Windows Helper is paired and an allowlisted action is explicitly approved by the user.
