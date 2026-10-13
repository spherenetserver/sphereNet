"""SphereNet plugin bridge example: post server events to a Discord webhook.

Connects to the plugin bridge (docs/PLUGINS.md), subscribes to a few events and posts a
line for each to a Discord channel webhook. Reconnects when the server restarts.
Python 3.8+, standard library only.

    python discord_webhook.py --port 2599 --token change-me --webhook https://discord.com/api/webhooks/...
"""
import argparse
import json
import socket
import time
import urllib.request

SUBSCRIBE = ["server.*", "save.completed", "char.death", "custom.*"]


def describe(event, data):
    """The Discord line for an event, or None to stay quiet."""
    if event == "server.started":
        return f"Server **{data['name']}** is up."
    if event == "server.stopping":
        return "Server is shutting down."
    if event == "save.completed" and not data["ok"]:
        return f"World save FAILED: {data['message']}"
    if event == "char.death" and data["victim"]["player"]:
        killer = data["killer"]
        by = f" by {killer['name']}" if killer else ""
        return f"{data['victim']['name']} was killed{by}."
    if event.startswith("custom."):
        return data.get("text") or event
    return None


def post(webhook, text):
    body = json.dumps({"content": text}).encode("utf-8")
    req = urllib.request.Request(webhook, data=body, headers={
        "Content-Type": "application/json",
        "User-Agent": "SphereNet-plugin",
    })
    try:
        urllib.request.urlopen(req, timeout=10).close()
    except Exception as e:  # Discord down or rate limited: keep following the server
        print("webhook failed:", e)


def run(host, port, token, webhook):
    with socket.create_connection((host, port), timeout=10) as sock:
        sock.settimeout(None)
        f = sock.makefile("rw", encoding="utf-8", newline="\n")
        f.write(json.dumps({"op": "auth", "token": token, "subscribe": SUBSCRIBE}) + "\n")
        f.flush()
        reply = json.loads(f.readline() or "{}")
        if not reply.get("ok"):
            raise SystemExit(f"bridge refused: {reply.get('error')}")
        print("connected, protocol", reply.get("version"))
        for line in f:
            msg = json.loads(line)
            if "event" not in msg:
                continue
            text = describe(msg["event"], msg.get("data") or {})
            if text:
                post(webhook, text)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--token", required=True)
    ap.add_argument("--webhook", required=True)
    args = ap.parse_args()
    while True:
        try:
            run(args.host, args.port, args.token, args.webhook)
            print("server closed the connection")
        except OSError as e:
            print("not connected:", e)
        time.sleep(5)


if __name__ == "__main__":
    main()
