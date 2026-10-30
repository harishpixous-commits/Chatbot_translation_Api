# DETERMINISTIC ENGINE-FAULT FIXTURE (not the model): item containing FAULT gets an Arabic-script tail.
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pixous_engine.script_check import check_items
class H(BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def _send(self,o,c=200):
        b=json.dumps(o,ensure_ascii=False).encode(); self.send_response(c); self.send_header("content-type","application/json"); self.send_header("content-length",str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self): self._send({"status":"ok"})
    def do_POST(self):
        n=int(self.headers["content-length"]); req=json.loads(self.rfile.read(n))
        outs=[("இது ஒரு சோதனை மொழிபெயர்ப்பு வாக்கியம் "+i+(" ڊائن" if "FAULT" in i else "")) for i in req["items"]]
        self._send({"translations":outs,"quality":check_items(req["target"],req["items"],outs),"provider":"fixture"})
HTTPServer(("127.0.0.1",8765),H).serve_forever()
