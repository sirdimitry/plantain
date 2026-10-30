import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
# FIX: порт берётся из окружения, чтобы занятый 18876 не ломал проверки.
PORT = int(os.environ.get('TEST_PORT', '18876'))
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def do_GET(self):
  # FIX: постоянная ошибка API для проверки, что 400 не повторяется.
  if self.path.startswith('/v1/disk'):
   self.send_response(400);self.send_header('Content-Length','0');self.end_headers()
   return
  data=b'abcdef'
  if self.path=='/slow':
   import time
   data=b'x'*(1024*1024)
   self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers()
   try:
    for i in range(0,len(data),1024):self.wfile.write(data[i:i+1024]);self.wfile.flush();time.sleep(.02)
   except (BrokenPipeError,ConnectionResetError):pass
   return
  ran=self.headers.get('Range')
  offset=int(ran[6:-1]) if ran else 0
  if self.path=='/ignore':offset=0
  if self.path=='/wrong':
   self.send_response(206);self.send_header('Content-Range','bytes 1-6/7')
  else:
   self.send_response(206 if offset else 200)
   if offset:self.send_header('Content-Range',f'bytes {offset}-5/6')
  self.send_header('Content-Length',str(len(data)-offset));self.end_headers();self.wfile.write(data[offset:])
ThreadingHTTPServer(('127.0.0.1',PORT),Handler).serve_forever()
