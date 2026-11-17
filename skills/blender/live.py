"""Run Python inside the Blender that is open on screen (the "MCP for Blender" add-on's server, localhost:9876).

    python live.py script.py      run a file in the live scene; its print() output comes back
    python live.py -c "print(1)"  run a line
    python live.py --ping         is the open Blender listening?
"""
import json, socket, sys

def send(command, port=9876):
    with socket.create_connection(("localhost", port), timeout=10) as s:
        s.settimeout(600)
        s.sendall(json.dumps(command).encode())
        buf = b""
        while True:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
            try:
                return json.loads(buf.decode())
            except (json.JSONDecodeError, UnicodeDecodeError):
                continue
    raise ConnectionError("Blender closed the connection without a reply")

def main(argv):
    if not argv:
        sys.exit(__doc__)
    if argv[0] == "--ping":
        command = {"type": "ping"}
    else:
        code = argv[1] if argv[0] == "-c" else open(argv[0], encoding="utf-8").read()
        command = {"type": "execute_code", "params": {"code": code}}
    try:
        reply = send(command)
    except OSError as e:
        sys.exit(f"No Blender is listening on localhost:9876 ({e}). In Blender: N panel > BlenderMCP > Connect, "
                 "or enable the 'MCP for Blender' add-on.")
    if reply.get("status") != "success":
        sys.exit(f"Blender error: {reply.get('message', reply)}")
    result = reply.get("result")
    print(result.get("result", "") if isinstance(result, dict) and "executed" in result else json.dumps(result))

if __name__ == "__main__":
    main(sys.argv[1:])
