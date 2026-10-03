import json, threading, time, uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

topics = {}
published = []
cond = threading.Condition()


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0))
        raw = self.rfile.read(n)
        path = self.path.split('?')[0].strip('/')
        if path == '':
            msg = json.loads(raw.decode('utf-8'))
            topic = msg['topic']
        else:
            topic = path
            msg = {'topic': topic, 'message': raw.decode('utf-8')}
        m = dict(msg)
        m.update({'id': uuid.uuid4().hex[:12], 'time': int(time.time()), 'event': 'message'})
        with cond:
            topics.setdefault(topic, []).append(m)
            published.append(m)
            cond.notify_all()
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{}')

    def do_GET(self):
        path = self.path.split('?')[0].strip('/').split('/')
        if path[0] == '_published':
            body = json.dumps(published).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(body)
            return
        topic = path[0]
        self.send_response(200)
        self.send_header('Content-Type', 'application/x-ndjson')
        self.end_headers()
        with cond:
            idx = len(topics.get(topic, []))

        def w(o):
            self.wfile.write((json.dumps(o) + '\n').encode())
            self.wfile.flush()

        try:
            w({'id': 'open', 'time': int(time.time()), 'event': 'open', 'topic': topic})
            while True:
                with cond:
                    cond.wait(timeout=5)
                    items = topics.get(topic, [])[idx:]
                    idx += len(items)
                for it in items:
                    w(it)
                if not items:
                    w({'id': 'ka', 'time': int(time.time()), 'event': 'keepalive', 'topic': topic})
        except Exception:
            pass


ThreadingHTTPServer(('127.0.0.1', 18080), H).serve_forever()
