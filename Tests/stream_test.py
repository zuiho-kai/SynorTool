"""使用本地模拟 OpenAI SSE 服务验证 C# 工具循环，不调用真实模型或钥匙串。"""
import json, pathlib, queue, subprocess, tempfile, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

repo = pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='synor-stream-tests-') as directory:
    root = pathlib.Path(directory)
    project = root / 'Bridge.csproj'
    project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup><ItemGroup><PackageReference Include="HtmlAgilityPack" Version="1.13.0"/><Compile Include="{repo}/WebFetch.cs"/><Compile Include="{repo}/ChatClient.cs"/><Compile Include="{repo}/AiSettings.cs"/><Compile Include="{repo}/AgentTools.cs"/><Content Include="{repo}/Tools/*" Link="Tools/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest"/></ItemGroup></Project>''')
    (root / 'Program.cs').write_text('while (await Console.In.ReadLineAsync() is { } line) ChatClient.Callback(line);')
    subprocess.run(['dotnet', 'build', str(project), '-v:q'], check=True)
    (root / 'bridge.c').write_text('''#include <stdio.h>
#include <stdlib.h>
#include <string.h>
void synor_chat_event(const char *s){puts(s);fflush(stdout);}
const char *synor_settings(const char *s){static char *saved;if(s){free(saved);saved=malloc(strlen(s)+32);sprintf(saved,"{\\"settings\\":%s}",s);}return saved?saved:"{\\"settings\\":null}";}
''')
    subprocess.run(['cc', '-dynamiclib', str(root / 'bridge.c'), '-o', str(root / 'bin/Debug/net10.0/libSynorPanel.dylib')], check=True)
    failures = []
    requests = []
    class Server(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_GET(self):
            if self.path in ['/redirect', '/loop']:
                self.send_response(302); self.send_header('Location', '/page' if self.path == '/redirect' else '/loop'); self.end_headers(); return
            self.send_response(404 if self.path == '/missing' else 200)
            kind = 'application/pdf' if self.path == '/binary' else 'text/plain' if self.path in ['/large', '/clip'] else 'text/html'
            self.send_header('Content-Type', kind); self.end_headers()
            data = '<html><head><meta charset="gbk"><title>测试网页</title></head><body><nav>NOISE</nav><main><h1>正文标题</h1><p>你好 <a href="/next">链接</a></p><script>BAD_SCRIPT</script><pre>a  b\nc</pre></main></body></html>'.encode('gbk')
            if self.path == '/large': data = b'x' * (2 * 1024 * 1024 + 1)
            if self.path == '/clip': data = b'x' * 60000
            try: self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError): pass
        def do_POST(self):
            try:
                raw = b''
                if self.headers.get('Transfer-Encoding') == 'chunked':
                    while True:
                        size = int(self.rfile.readline().strip(), 16)
                        if not size: self.rfile.readline(); break
                        raw += self.rfile.read(size); self.rfile.read(2)
                else: raw = self.rfile.read(int(self.headers['Content-Length']))
                body = json.loads(raw); requests.append(body)
                messages = body['messages']; outputs = [m for m in messages if m['role'] == 'tool']
                if any('auth-test' in str(m.get('content')) for m in messages):
                    self.send_response(401); self.send_header('Content-Type', 'application/json'); self.end_headers()
                    self.wfile.write(json.dumps({'error':{'message':'unauthorized client detected'}}).encode()); return
                self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.end_headers()
                def event(delta, reason=None):
                    self.wfile.write(('data: '+json.dumps({'choices':[{'index':0,'delta':delta,'finish_reason':reason}]})+'\n\n').encode()); self.wfile.flush()
                def tools(calls, reason='tool_calls'):
                    # 模拟参数和名称跨多个 SSE 片段，并交错多个 index。
                    for i, (name, args) in enumerate(calls):
                        event({'tool_calls':[{'index':i,'id':f'call-{len(outputs)}-{i}','type':'function','function':{'name':name[:1],'arguments':''}}]})
                    for i, (name, args) in enumerate(calls):
                        event({'tool_calls':[{'index':i,'function':{'name':name[1:],'arguments':args[:3]}}]})
                    for i, (name, args) in enumerate(calls):
                        event({'tool_calls':[{'index':i,'function':{'arguments':args[3:]}}]})
                    event({}, reason)
                if 'tools' not in body:
                    event({'content':'translation-ok'}, 'stop')
                elif any('length-test' in str(m.get('content')) for m in messages):
                    tools([('write',json.dumps({'path':'must-not-exist','content':'bad'}))], 'length')
                elif any('invalid-test' in str(m.get('content')) for m in messages):
                    if not outputs: tools([('write','{broken')])
                    else:
                        assert 'Invalid tool arguments' in outputs[0]['content']
                        event({'content':'handled-invalid'}, 'stop')
                elif any('fetch-test' in str(m.get('content')) for m in messages):
                    if not outputs:
                        urls = [f'http://127.0.0.1:{self.server.server_port}/{path}' for path in ['redirect','loop','binary','large','clip','missing']]
                        urls += ['file:///etc/hosts', 'http://user:password@localhost/']
                        tools([('web_fetch', json.dumps({'url':url})) for url in urls])
                    else:
                        assert len(outputs) == 8
                        page = outputs[0]['content']
                        assert '正文标题' in page and '测试网页' in page and '你好' in page, page
                        assert '/page' in page and f'](http://127.0.0.1:{self.server.server_port}/next)' in page
                        assert 'NOISE' not in page and 'BAD_SCRIPT' not in page and 'a  b\nc' in page
                        for i, expected in [(1,'重定向'),(2,'不支持'),(3,'2 MB'),(4,'truncated'),(5,'404'),(6,'HTTP(S)'),(7,'HTTP(S)')]:
                            assert expected in outputs[i]['content'], outputs[i]
                        event({'content':'fetch-ok'}, 'stop')
                elif not outputs:
                    assert len(body['tools']) == 8
                    assert str(root) in messages[0]['content']
                    tools([('write',json.dumps({'path':'sample.txt','content':'alpha'})),('edit',json.dumps({'path':'sample.txt','edits':[{'oldText':'alpha','newText':'beta'}]}))])
                elif len(outputs) == 2:
                    assert (root/'sample.txt').read_text() == 'beta'
                    tools([('read',json.dumps({'path':'sample.txt'})),('bash',json.dumps({'command':'printf tool-ok'}))])
                else:
                    assert len(outputs) == 4
                    assert 'beta' in outputs[2]['content'] and 'tool-ok' in outputs[3]['content']
                    pending = set()
                    for message in messages:
                        if message['role']=='assistant':
                            assert not pending
                            pending = {c['id'] for c in message.get('tool_calls',[])}
                        elif message['role']=='tool': pending.remove(message['tool_call_id'])
                        else: assert not pending
                    assert not pending
                    event({'content':'all-tools-ok'}, 'stop')
                self.wfile.write(b'data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}\n\ndata: [DONE]\n\n'); self.wfile.flush()
            except Exception as error: failures.append(repr(error))
    server = ThreadingHTTPServer(('127.0.0.1',0), Server)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    process = subprocess.Popen(['dotnet',str(root/'bin/Debug/net10.0/Bridge.dll')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,bufsize=1)
    events = queue.Queue()
    def read():
        for line in process.stdout: events.put(json.loads(line))
    threading.Thread(target=read,daemon=True).start()
    def send(value): process.stdin.write(json.dumps(value)+'\n'); process.stdin.flush()
    def collect(identifier):
        result=[]
        while True:
            event=events.get(timeout=20)
            if event.get('id')!=identifier:continue
            result.append(event)
            if event.get('done') or event.get('error') or event.get('settings'):return result
    try:
        send({'type':'settings-save','id':'settings','settings':{'BaseUrl':f'http://127.0.0.1:{server.server_port}/v1','Model':'test','WorkingDirectory':str(root)}})
        assert 'settings' in collect('settings')[-1]
        for name in ['normal','invalid-test','length-test','fetch-test','auth-test']:
            send({'type':'chat','id':name,'messages':[{'role':'user','content':name}]})
            result=collect(name)
            assert not failures, failures
            if name=='normal':
                assert result[-1].get('done'), result[-1]
                assert [e['tool']['status'] for e in result if 'tool' in e].count('done')==4
                assert [e['usage'] for e in result if e.get('usage')][-1]['completion_tokens']==15
                assert result[-1]['generationMs']<=result[-1]['elapsedMs']
                assert len([e for e in result if 'apiMessages' in e][-1]['apiMessages'])==7
            elif name in ['invalid-test','fetch-test']: assert result[-1].get('done'), result[-1]
            elif name=='auth-test': assert 'unauthorized client detected' in result[-1].get('error',''), result[-1]
            else: assert result[-1].get('error') and not (root/'must-not-exist').exists()
            print('PASS',name,flush=True)
        send({'type':'translate','id':'translation','text':'你好','target':'English'})
        result=collect('translation')
        assert result[-1].get('done') and 'tools' not in requests[-1]
        print('PASS translation does not expose tools',flush=True)
    finally:
        process.terminate();process.wait(timeout=5);server.shutdown()
