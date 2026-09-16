"""使用本地模拟 OpenAI SSE 服务验证 C# 工具循环，不调用真实模型或钥匙串。"""
import json, os, pathlib, queue, subprocess, tempfile, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

repo = pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='synor-stream-tests-') as directory:
    root = pathlib.Path(directory)
    project = root / 'Bridge.csproj'
    project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup><ItemGroup><PackageReference Include="HtmlAgilityPack" Version="1.13.0"/><Compile Include="{repo}/WebFetch.cs"/><Compile Include="{repo}/ChatClient.cs"/><Compile Include="{repo}/AiSettings.cs"/><Compile Include="{repo}/ModelCatalog.cs"/><Compile Include="{repo}/PiAi/Core.cs"/><Compile Include="{repo}/PiAi/OpenAiAdapters.cs"/><Compile Include="{repo}/AgentTools.cs"/><Content Include="{repo}/Tools/*" Link="Tools/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest"/></ItemGroup></Project>''')
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
    balance_calls = []
    balance_response = {"amount":"110.00", "status":200}
    class Server(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_GET(self):
            if self.path == '/user/balance':
                balance_calls.append(self.path)
                authorized = self.headers.get('Authorization') == 'Bearer balance-test'
                self.send_response(balance_response["status"] if authorized else 401); self.send_header('Content-Type', 'application/json'); self.end_headers()
                self.wfile.write(json.dumps({'balance_infos':[{'currency':'CNY','total_balance':balance_response['amount']}]}).encode()); return
            if self.path == '/v1/models':
                if self.headers.get('Authorization') == 'Bearer bad-key':
                    self.send_response(401); self.send_header('Content-Type', 'application/json'); self.end_headers()
                    self.wfile.write(json.dumps({'error':{'message':'invalid api key'}}).encode()); return
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
                self.wfile.write(json.dumps({'object':'list','data':[{'id':'alpha-model'},{'id':'beta-model'},{'id':'alpha-model'}]}).encode()); return
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
                assert self.path == '/v1/responses', self.path
                assert 'messages' not in body and body['store'] is False
                assert 'reasoning.encrypted_content' in body['include']
                messages = body['input']
                outputs = [{'content':m['output']} for m in messages if m.get('type') == 'function_call_output']
                pending=set()
                for message in messages:
                    if message.get('type')=='function_call':
                        assert message['call_id'] not in pending
                        pending.add(message['call_id'])
                    elif message.get('type')=='function_call_output':
                        assert message['call_id'] in pending, 'orphan tool result'
                        pending.remove(message['call_id'])
                    elif message.get('role'): assert not pending, 'message splits tool batch'
                assert not pending, 'missing tool result'
                if outputs:
                    assert any(m.get('encrypted_content')=='opaque' for m in messages), 'missing reasoning replay'
                if any('auth-test' in str(m.get('content')) for m in messages):
                    self.send_response(401); self.send_header('Content-Type', 'application/json'); self.end_headers()
                    self.wfile.write(json.dumps({'error':{'message':'unauthorized client detected'}}).encode()); return
                self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.end_headers()
                def response_event(value):
                    self.wfile.write(('data: '+json.dumps(value)+'\n\n').encode()); self.wfile.flush()
                def event(delta, reason=None):
                    if delta.get('content'):
                        response_event({'type':'response.output_text.delta','delta':delta['content']})
                    if reason:
                        response_event({'type':'response.incomplete' if reason=='length' else 'response.completed','response':{
                            'incomplete_details':{'reason':'max_output_tokens'},
                            'output':[{'type':'reasoning','id':f'rs_{len(outputs)}','encrypted_content':'opaque','summary':[]}],
                            'usage':{'input_tokens':10,'output_tokens':5,'total_tokens':15}}})
                def tools(calls, reason='tool_calls'):
                    # Responses 的 item_id/call_id 映射、交错参数增量及 done 内容去重。
                    for i, (name, args) in enumerate(calls):
                        response_event({'type':'response.output_item.added','item':{'type':'function_call','id':f'fc_{len(outputs)}_{i}','call_id':f'call-{len(outputs)}-{i}','name':name,'arguments':''}})
                    for segment in [slice(0,3),slice(3,None)]:
                        for i, (name,args) in enumerate(calls):
                            response_event({'type':'response.function_call_arguments.delta','item_id':f'fc_{len(outputs)}_{i}','delta':args[segment]})
                    for i, (name,args) in enumerate(calls):
                        response_event({'type':'response.function_call_arguments.done','item_id':f'fc_{len(outputs)}_{i}','arguments':args})
                        response_event({'type':'response.output_item.done','item':{'type':'function_call','id':f'fc_{len(outputs)}_{i}','call_id':f'call-{len(outputs)}-{i}','name':name,'arguments':args}})
                    event({},reason)
                if any('eof-test' in str(m.get('content')) for m in messages):
                    event({'content':'partial'})
                    return
                elif any('text-length-test' in str(m.get('content')) for m in messages):
                    event({'content':'partial-length'},'length')
                elif any('schema-test' in str(m.get('content')) for m in messages):
                    if not outputs: tools([('write',json.dumps({'path':'schema-never'})),('read',json.dumps({'path':'missing','offset':0})),('unknown', '{}'),('ls','{}')])
                    else:
                        assert len(outputs)==4 and 'Invalid tool arguments' in outputs[0]['content'] and 'Invalid tool arguments' in outputs[1]['content']
                        assert 'not found' in outputs[2]['content'] and not (root/'schema-never').exists()
                        event({'content':'schema-recovered'}, 'stop')
                elif any('after-cancel-test' in str(m.get('content')) for m in messages):
                    event({'content':'continued'}, 'stop')
                elif any('cancel-test' in str(m.get('content')) for m in messages):
                    tools([('bash',json.dumps({'command':'echo started; sleep 1; echo ready; sleep 30; touch cancel-never'})),('write',json.dumps({'path':'cancel-second-never','content':'bad'}))])
                elif 'tools' not in body:
                    event({'content':'translation-ok'}, 'stop')
                elif any('length-test' in str(m.get('content')) for m in messages):
                    if not outputs: tools([('write',json.dumps({'path':'must-not-exist','content':'bad'}))], 'length')
                    else:
                        assert 'not executed' in outputs[0]['content'] and not (root/'must-not-exist').exists()
                        event({'content':'recovered-truncated-call'}, 'stop')
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
                    event({'content':'all-tools-ok'}, 'stop')
            except Exception as error: failures.append(repr(error))
    server = ThreadingHTTPServer(('127.0.0.1',0), Server)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    (root/'settings').mkdir()
    (root/'settings/settings.json').write_text(json.dumps({'ApiMode':'ChatCompletions','Model':'legacy-model','WorkingDirectory':str(root)}))
    process = subprocess.Popen(['dotnet',str(root/'bin/Debug/net10.0/Bridge.dll')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,bufsize=1,env={**os.environ,'SYNOR_SETTINGS_DIR':str(root/'settings')})
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
            if event.get('done') or event.get('error') or event.get('settings') or event.get('models') or 'balance' in event:return result
    try:
        send({'type':'settings-load','id':'legacy-settings'})
        legacy=collect('legacy-settings')[-1]['settings']
        assert legacy['Model']=='legacy-model' and 'ApiMode' not in legacy
        print('PASS legacy settings load without an API mode',flush=True)
        send({'type':'settings-save','id':'settings','settings':{'BaseUrl':f'http://127.0.0.1:{server.server_port}/v1','Model':'test','WorkingDirectory':str(root)}})
        assert 'settings' in collect('settings')[-1]
        balance_settings = {'BalanceUrl':f'http://127.0.0.1:{server.server_port}/user/balance','ApiKey':'balance-test'}
        send({'type':'balance-load','id':'balance','settings':balance_settings})
        result=collect('balance')[-1]
        assert result.get('balance')=='110.00' and result.get('currency')=='CNY', result
        print('PASS balance-load uses Bearer key and DeepSeek mapping',flush=True)
        for name, overrides, expected in [('balance-auth',{'ApiKey':'wrong'},'HTTP 401'),('balance-missing',{'BalancePath':'data.balance'},'响应缺少字段'),('balance-invalid-json',{'BalanceUrl':f'http://127.0.0.1:{server.server_port}/page'},'余额查询失败'),('balance-redirect',{'BalanceUrl':f'http://127.0.0.1:{server.server_port}/redirect'},'HTTP 302')]:
            send({'type':'balance-load','id':name,'settings':{**balance_settings,**overrides}})
            result=collect(name)[-1]
            assert expected in result.get('error','') and 'balance' not in result, result
            print('PASS',name,flush=True)
        send({'type':'settings-load','id':'balance-settings'})
        assert collect('balance-settings')[-1]['settings']['BalanceUrl']=='', 'balance query must not save settings'
        print('PASS balance query leaves saved settings unchanged',flush=True)
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
            elif name in ['invalid-test','fetch-test','length-test']: assert result[-1].get('done'), result[-1]
            elif name=='auth-test': assert 'unauthorized client detected' in result[-1].get('error',''), result[-1]
            else: assert result[-1].get('error') and not (root/'must-not-exist').exists()
            print('PASS',name,flush=True)
        send({'type':'translate','id':'translation','text':'你好','target':'English'})
        result=collect('translation')
        assert result[-1].get('done') and 'tools' not in requests[-1]
        print('PASS translation does not expose tools',flush=True)
        send({'type':'models-load','id':'models','baseUrl':f'http://127.0.0.1:{server.server_port}/v1','apiKey':'','userAgent':''})
        result=collect('models')
        assert result[-1].get('models')==['alpha-model','beta-model'], result[-1]
        print('PASS models-load lists models without touching settings',flush=True)
        send({'type':'models-load','id':'models-denied','baseUrl':f'http://127.0.0.1:{server.server_port}/v1','apiKey':'bad-key'})
        result=collect('models-denied')
        assert 'HTTP 401' in result[-1].get('error',''), result[-1]
        print('PASS models-load reports auth failure',flush=True)
        send({'type':'models-load','id':'models-bad-url','baseUrl':'not-a-url'})
        result=collect('models-bad-url')
        assert '基础地址' in result[-1].get('error',''), result[-1]
        print('PASS models-load validates base url',flush=True)
        for name in ['schema-test','eof-test','text-length-test']:
            send({'type':'chat','id':name,'messages':[{'role':'user','content':name}]})
            result=collect(name)
            assert not failures, failures
            if name=='eof-test': assert result[-1].get('error') and not result[-1].get('done')
            elif name=='text-length-test': assert result[-1].get('done') and result[-1]['stopReason']=='Length'
            else: assert result[-1].get('done')
            print('PASS',name,flush=True)
        send({'type':'chat','id':'cancel','messages':[{'role':'user','content':'cancel-test'}]})
        while True:
            event=events.get(timeout=20)
            if 'ready' in event.get('tool',{}).get('output',''): break
        send({'type':'cancel','id':'cancel'})
        cancelled=collect('cancel')
        assert cancelled[-1].get('error') and not (root/'cancel-never').exists() and not (root/'cancel-second-never').exists()
        replay=[e['apiMessages'] for e in cancelled if 'apiMessages' in e][-1]
        assert len([m for m in replay if m['role']=='tool'])==2
        send({'type':'chat','id':'after-cancel','messages':[{'role':'user','content':'normal'},*replay,{'role':'user','content':'after-cancel-test'}]})
        assert collect('after-cancel')[-1].get('error') is None, failures
        print('PASS cancellation stops shell and remaining calls; replay keeps complete pairs',flush=True)
        send({'type':'settings-load','id':'flow-settings'})
        flow_settings=collect('flow-settings')[-1]['settings']
        send({'type':'settings-save','id':'small-context','settings':{**flow_settings,'ContextCompressionBytes':16384}})
        collect('small-context')
        send({'type':'chat','id':'compact','messages':[{'role':'user','content':'x'*20000},{'role':'assistant','content':None,'tool_calls':[{'id':'old-call','type':'function','function':{'name':'read','arguments':'{}'}}]},{'role':'tool','tool_call_id':'old-call','content':'x'*15000},{'role':'user','content':'normal'}]})
        assert collect('compact')[-1].get('done') and not failures, failures
        print('PASS context trimming preserves tool pairs',flush=True)
        send({'type':'settings-save','id':'restore-settings','settings':flow_settings})
        collect('restore-settings')
        # 自动余额仅在已保存配置手动查询成功后启用，每个完整工具回合仅请求一次。
        send({'type':'settings-load','id':'before-balance'})
        saved = collect('before-balance')[-1]['settings']
        saved.update(balance_settings)
        send({'type':'settings-save','id':'save-balance','settings':saved})
        assert collect('save-balance')[-1]['settings']['LastBalance'] is None
        count=len(balance_calls)
        send({'type':'chat','id':'not-enabled','messages':[{'role':'user','content':'normal'}]})
        assert collect('not-enabled')[-1].get('done')
        send({'type':'settings-load','id':'still-disabled'})
        assert collect('still-disabled')[-1]['settings']['LastBalance'] is None and len(balance_calls)==count
        print('PASS saved configuration alone does not activate auto balance',flush=True)
        send({'type':'balance-load','id':'activate-balance','settings':saved})
        assert collect('activate-balance')[-1]['balanceChanged']
        for name, status, amount in [('auto-success',200,'109.25'),('auto-failure',503,'109.25')]:
            balance_response.update(status=status,amount=amount)
            count=len(balance_calls)
            send({'type':'chat','id':name,'messages':[{'role':'user','content':'normal'}]})
            assert collect(name)[-1].get('done')
            update=events.get(timeout=20)
            assert update.get('balanceChanged'), update
            assert len(balance_calls)==count+1, balance_calls
            send({'type':'settings-load','id':name+'-read'})
            snapshot=collect(name+'-read')[-1]['settings']['LastBalance']
            assert snapshot['Amount']=='109.25' and snapshot['Currency']=='CNY', snapshot
            assert bool(snapshot['Error'])==(status!=200), snapshot
            print('PASS',name,'refreshes once after tool round and retains balance on failure',flush=True)
        stored=json.loads((root/'settings/settings.json').read_text())
        assert stored['LastBalance']['Amount']=='109.25'
        send({'type':'settings-save','id':'save-stale-form','settings':saved})
        assert collect('save-stale-form')[-1]['settings']['LastBalance']['Amount']=='109.25'
        print('PASS balance persists on disk and survives saving an old settings form',flush=True)
        saved['ApiKey']='different-key'
        send({'type':'settings-save','id':'switch-account','settings':saved})
        assert collect('switch-account')[-1]['settings']['LastBalance'] is None
        print('PASS changing API key clears balance and auto activation',flush=True)
    finally:
        process.terminate();process.wait(timeout=5);server.shutdown()
