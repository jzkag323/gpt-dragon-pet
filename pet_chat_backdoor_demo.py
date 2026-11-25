"""
GPT 龙娘桌宠 - Python 本地 HTTP 聊天后门示例（零第三方依赖）
启动方式：
    python pet_chat_backdoor_demo.py

在桌宠聊天窗口 ->「接口与人设」中：
    1. 后端模式选择：本地 HTTP 后门
    2. 地址填：http://127.0.0.1:8765/chat
    3. 点「保存配置」和「测试连通」即可！

你可以在 generate_reply() 里自由接入：
- OpenAI / DeepSeek / Claude / 本地 Ollama SDK
- LangChain / MCP / 自定义工具调用
- 电脑状态查询、定时提醒、桌面控制等任意 Python 逻辑
"""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from typing import Tuple
import urllib.request

HOST = "127.0.0.1"
PORT = 8765


def generate_reply(message: str, persona: str, history: list) -> Tuple[str, str]:
    """返回 (回复文本, 动作情绪)。
    mood 可选：normal / wave / curious / celebrate / sleep
    """
    text = message.strip()
    if text.lower() == "ping":
        return "Python 本地后门已连通，随时待命！", "celebrate"

    # 如果设置了 OPENAI_API_KEY，也可以在这里转发给任意 OpenAI 兼容接口
    api_key = os.environ.get("OPENAI_API_KEY", "").strip()
    api_base = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
    model = os.environ.get("OPENAI_MODEL", "gpt-4o-mini")

    if api_key:
        messages = []
        if persona:
            messages.append({"role": "system", "content": persona})
        for item in history[-20:]:
            role = "assistant" if item.get("role") == "assistant" else "user"
            content = str(item.get("content", "")).strip()
            if content:
                messages.append({"role": role, "content": content})
        messages.append({"role": "user", "content": text})

        req = urllib.request.Request(
            f"{api_base}/chat/completions",
            data=json.dumps({"model": model, "messages": messages, "temperature": 0.7}).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=60) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        reply = data["choices"][0]["message"]["content"].strip()
        return reply, "wave"

    # 默认离线回声示例（未配置 OPENAI_API_KEY 时）
    if any(k in text for k in ("你好", "嗨", "hi", "hello")):
        return "你好呀！我现在正连着你的 Python 本地后门，随时可以给我接上任何大模型或工具~", "wave"
    if any(k in text for k in ("晚安", "睡", "累")):
        return "辛苦啦，那我先乖乖打个盹，有事随时叫我！", "sleep"
    if any(k in text for k in ("完成", "成功", "太棒", "厉害")):
        return "好耶！给你放个星光庆祝一下！", "celebrate"
    return f"收到你的消息啦：「{text}」（当前为 Python 本地后门示例模式，可在脚本里接入任意智能体或设置 OPENAI_API_KEY）", "curious"


class PetChatHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path.rstrip("/") != "/chat":
            self._send_json(404, {"error": "not found, use POST /chat"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            raw = self.rfile.read(length).decode("utf-8")
            payload = json.loads(raw) if raw else {}
            message = str(payload.get("message", ""))
            persona = str(payload.get("persona", ""))
            history = payload.get("history", [])
            if not isinstance(history, list):
                history = []

            reply, mood = generate_reply(message, persona, history)
            self._send_json(200, {"reply": reply, "mood": mood})
        except Exception as exc:
            self._send_json(500, {"error": str(exc)})

    def _send_json(self, status: int, body: dict):
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt, *args):
        print(f"[PetBackdoor] {self.address_string()} - {fmt % args}")


if __name__ == "__main__":
    server = ThreadingHTTPServer((HOST, PORT), PetChatHandler)
    print(f"GPT 龙娘 Python 本地后门已启动: http://{HOST}:{PORT}/chat")
    print("按 Ctrl+C 停止。")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")
