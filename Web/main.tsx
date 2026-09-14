import { createRoot } from 'react-dom/client';
import { useEffect, useState, useRef } from 'react';
import { AssistantRuntimeProvider, useLocalRuntime, ThreadPrimitive, MessagePrimitive, ComposerPrimitive, AttachmentPrimitive, SimpleImageAttachmentAdapter, useAuiState, useAui, type ChatModelAdapter, type ReasoningMessagePartProps, type TextMessagePartProps } from '@assistant-ui/react';
import { Streamdown, defaultRemarkPlugins } from 'streamdown';
import { createMathPlugin } from '@streamdown/math';
import { Settings } from './Settings';
import { Translation } from './Translation';

declare global { interface Window { __SYNOR_TRANSLATION__?:boolean; webkit?: { messageHandlers: { synor: { postMessage(value: {type:string; text?:string; id?:string; thinking?:boolean; target?:string; settings?:unknown; messages?:unknown[]}):void } } } } }
type ToolState = {callId:string;name:string;arguments:string;status:'running'|'done'|'error';output?:string;image?:string};
type TokenUsage = {prompt_tokens:number;completion_tokens:number;total_tokens:number};
const adapter: ChatModelAdapter = {
  async *run({messages, abortSignal, runConfig}) {
    const bridge = window.webkit?.messageHandlers.synor;
    if (!bridge) throw new Error('请在 Synor 原生窗口中使用聊天。');
    const id = crypto.randomUUID();
    const queue: {tool?:Partial<ToolState> & {callId:string};apiMessages?:unknown[];generationMs?:number;elapsedMs?:number;firstTokenMs?:number;usage?:TokenUsage;reasoning?:string;text?:string;done?:boolean;error?:string}[] = [];
    let wake = ()=>{};
    let completed = false;
    const receive = (event:Event)=>{
      const payload = (event as CustomEvent).detail;
      if (payload.id === id) {queue.push(payload);wake();}
    };
    const cancel = ()=>{bridge.postMessage({type:'cancel',id});wake();};
    window.addEventListener('synor-chat',receive);
    abortSignal.addEventListener('abort',cancel,{once:true});
    try {
      if (abortSignal.aborted) return;
      bridge.postMessage({type:'chat',id,thinking:runConfig.custom?.thinking !== false,messages:messages.flatMap(message=>{
        const stored=message.metadata?.custom?.apiMessages;
        if(message.role==='assistant' && Array.isArray(stored))return stored;
        return [{role:message.role,
          content:[...message.content,...(message.role==='user' ? message.attachments?.flatMap(a=>a.content) ?? [] : [])]
            .flatMap<{type:'text';text:string} | {type:'image_url';image_url:{url:string}}>(part=>part.type==='text' ? [{type:'text',text:part.text}] : part.type==='image' ? [{type:'image_url',image_url:{url:part.image}}] : [])}];
      })});
      let text = '';
      let reasoning = '';
      let firstTokenMs: number | undefined;
      let usage: TokenUsage | undefined;
      let elapsedMs: number | undefined;
      let generationMs: number | undefined;
      let apiMessages: unknown[] | undefined;
      const tools: ToolState[] = [];
      while (!abortSignal.aborted) {
        if (!queue.length) await new Promise<void>(resolve=>{wake=resolve;});
        if (abortSignal.aborted) return;
        while (queue.length) {
          const event=queue.shift()!;
          if (event.error) throw new Error(event.error);
          if (event.reasoning || event.text || 'usage' in event || event.done || event.tool || event.apiMessages) {
            if(event.tool){
              const current=tools.find(tool=>tool.callId===event.tool!.callId);
              if(current)Object.assign(current,event.tool);
              else tools.push({name:'tool',arguments:'',status:'running',...event.tool});
            }
            apiMessages=event.apiMessages ?? apiMessages;
            generationMs=event.generationMs ?? generationMs;
            elapsedMs = event.elapsedMs ?? elapsedMs;
            firstTokenMs ??= event.firstTokenMs;
            if('usage' in event)usage = event.usage ?? undefined;
            reasoning+=event.reasoning ?? '';
            text+=event.text ?? '';
            yield {metadata:{custom:{firstTokenMs,usage,elapsedMs,generationMs,apiMessages,tools:tools.map(tool=>({...tool}))}},content:[
              ...(reasoning ? [{type:'reasoning' as const,text:reasoning}] : []),
              ...(text ? [{type:'text' as const,text}] : [])
            ]};
          }
          if (event.done) {if (!text && !tools.length) throw new Error('模型没有返回正文，请重试。');completed=true;return;}
        }
      }
    } finally {
      window.removeEventListener('synor-chat',receive);
      abortSignal.removeEventListener('abort',cancel);
      if (!completed) bridge.postMessage({type:'cancel',id});
    }
  }
};
const math = createMathPlugin({singleDollarTextMath:true});
type MathNode = {type:string;value?:string;children?:MathNode[];data?:{hChildren?:MathNode[]}};
function normalizeMathPercent() {
  return function visit(node:MathNode) {
    if((node.type==='math' || node.type==='inlineMath') && node.value){
      // 兼容模型输出的 $20%$；仅处理公式末尾的数字百分号，不改代码或 LaTeX 注释。
      node.value=node.value.replace(/(\d)%(?=\s*$)/g,'$1\\%');
      for(const child of node.data?.hChildren ?? [])if(child.type==='text')child.value=node.value;
    }
    node.children?.forEach(visit);
  };
}
const mathRemarkPlugins = [...Object.values(defaultRemarkPlugins), normalizeMathPercent];
function Markdown({text}:TextMessagePartProps) {
  const running = useAuiState(s=>s.message.status?.type === 'running');
  return <Streamdown plugins={{math}} remarkPlugins={mathRemarkPlugins} skipHtml controls={false} isAnimating={running} components={{
    a:({children})=><span className="link">{children}</span>,
    img:()=> <span>（不加载远程图片）</span>,
    pre:({children})=><pre>{children}</pre>,
    table:({children})=><div className="table-scroll"><table>{children}</table></div>
  }}>{text}</Streamdown>;
}
function Reasoning({text}:ReasoningMessagePartProps) {
  const thinking = useAuiState(s=>s.message.status?.type === 'running' && !s.message.content.some(p=>p.type === 'text' && p.text));
  const interrupted = useAuiState(s=>s.message.status?.type === 'incomplete');
  const [open,setOpen] = useState(thinking);
  const body = useRef<HTMLDivElement>(null);
  const follow = useRef(true);
  useEffect(()=>{setOpen(thinking);},[thinking]);
  useEffect(()=>{
    if (thinking && open && follow.current && body.current) body.current.scrollTop=body.current.scrollHeight;
  },[text,thinking,open]);
  return <section className="reasoning"><button type="button" className="reasoning-toggle" aria-expanded={open} onClick={()=>setOpen(!open)}>
    <span aria-hidden="true">{open ? '⌄' : '›'}</span>{thinking ? '正在思考…' : interrupted ? '思考已中断' : '思考过程'}
  </button>{open && <div className="reasoning-body" ref={body} onScroll={event=>{
    const element=event.currentTarget;
    follow.current=element.scrollHeight-element.scrollTop-element.clientHeight<24;
  }}>{text}</div>}</section>;
}
function AssistantMessage() {
  const text = useAuiState(s=>s.message.content.filter(p=>p.type==='text').map(p=>p.text).join(''));
  const running = useAuiState(s=>s.message.status?.type === 'running');
  const hasReasoning = useAuiState(s=>s.message.content.some(p=>p.type === 'reasoning' && p.text));
  const error = useAuiState(s=>{
    const status=s.message.status;
    if (status?.type !== 'incomplete' || status.reason !== 'error') return '';
    const value=status.error;
    return typeof value === 'string' ? value : value && typeof value === 'object' && 'message' in value ? String(value.message) : '请求失败，请重试。';
  });
  const firstTokenMs = useAuiState(s=>s.message.metadata.custom.firstTokenMs) as number | undefined;
  const usage = useAuiState(s=>s.message.metadata.custom.usage) as TokenUsage | undefined;
  const elapsedMs = useAuiState(s=>s.message.metadata.custom.elapsedMs) as number | undefined;
  const measuredGenerationMs = useAuiState(s=>s.message.metadata.custom.generationMs) as number | undefined;
  const tools = useAuiState(s=>s.message.metadata.custom.tools) as ToolState[] | undefined;
  const generationMs = measuredGenerationMs ?? (elapsedMs !== undefined && firstTokenMs !== undefined ? elapsedMs-firstTokenMs : 0);
  const speed = usage && generationMs>0 ? usage.completion_tokens/(generationMs/1000) : undefined;
  const [copied,setCopied] = useState(false);
  return <MessagePrimitive.Root className="assistant message"><div className="byline"><span className="spark">✦</span> SYNOR <span className="badge">AI</span></div>{tools?.map(tool=><details className="tool-call" key={tool.callId} open={tool.status==='running' && running ? true : undefined}><summary><span className="tool-name">{tool.name}</span><span>{tool.status==='running' ? running ? '执行中…' : '已中断' : tool.status==='error' ? '执行失败' : '已完成'}</span></summary><pre className="tool-arguments">{tool.arguments}</pre>{tool.output && <pre className="tool-output">{tool.output}</pre>}{tool.image && <img className="tool-image" src={tool.image} alt="工具读取的图片"/>}</details>)}<div className="markdown"><MessagePrimitive.Parts components={{Text:Markdown,Reasoning}}/></div>{running && !text && !hasReasoning && !tools?.length && <p>正在生成…</p>}{error && <p role="alert">{error}</p>}<div className="answer-actions"><button className="copy" onClick={()=>{ window.webkit?.messageHandlers.synor.postMessage({type:'copy',text}); setCopied(true); }}>{copied?'已复制 ✓':'复制回答'}</button><span className="answer-stats">
    <span title="从发起请求到收到首个思考或正文片段的本地耗时">首 Token {firstTokenMs === undefined ? '—' : `${(firstTokenMs/1000).toFixed(2)} s`}</span>
    <span title="服务端输出 Token 数 ÷ 各轮首个输出到流结束的耗时之和，排除工具执行时间；包括服务端计入的思考与工具调用 Token">速度 {speed === undefined ? (running ? '统计中…' : '—') : `${speed.toFixed(1)} Token/s`}</span>
    <span>{usage ? `Token 输入 ${usage.prompt_tokens} / 输出 ${usage.completion_tokens} / 合计 ${usage.total_tokens}` : running ? 'Token 统计中…' : 'Token 未返回'}</span>
  </span></div></MessagePrimitive.Root>;
}
function ImageAttachment() {
  const attachment = useAuiState(s=>s.attachment);
  const [preview,setPreview] = useState('');
  useEffect(()=>{
    if (attachment.file) {
      const url = URL.createObjectURL(attachment.file);
      setPreview(url);
      return ()=>URL.revokeObjectURL(url);
    }
    const image = attachment.content?.find(p=>p.type==='image');
    setPreview(image?.type==='image' ? image.image : '');
  },[attachment.file,attachment.content]);
  return <AttachmentPrimitive.Root className="image-attachment">{preview && <img src={preview} alt={attachment.name || '粘贴的图片'}/>}<AttachmentPrimitive.Remove className="remove-image" aria-label="移除图片">×</AttachmentPrimitive.Remove></AttachmentPrimitive.Root>;
}
function UserMessage() { return <MessagePrimitive.Root className="user message"><div className="attachments"><MessagePrimitive.Attachments components={{Attachment:ImageAttachment}}/></div><MessagePrimitive.Parts /></MessagePrimitive.Root>; }
function Chat() {
  const aui = useAui();
  const thinking = useAuiState(s=>s.composer.runConfig.custom?.thinking !== false);
  const running = useAuiState(s=>s.thread.isRunning);
  const [config,setConfig] = useState<{Model:string;ReasoningControl:boolean}>();
  const [pasteError,setPasteError] = useState('');
  const clearPending = useRef(false);
  useEffect(()=>{
    if (!running && clearPending.current) {
      clearPending.current=false;
      aui.thread.reset();
    }
  },[aui,running]);
  useEffect(()=>{
    const clear=()=>{
      if(document.querySelector('dialog[open]'))return;
      if(aui.thread.getState().isRunning){clearPending.current=true;aui.thread.cancelRun();}
      else aui.thread.reset();
    };
    window.addEventListener('synor-clear-context',clear);
    return ()=>window.removeEventListener('synor-clear-context',clear);
  },[aui]);
  useEffect(()=>{
    const receive = async (event:Event)=>{
      const payload = (event as CustomEvent<{image?:string;text?:string;error?:string}>).detail;
      setPasteError('');
      try {
        if (payload.error) throw new Error(payload.error);
        if (payload.image) {
          const bytes = Uint8Array.from(atob(payload.image),c=>c.charCodeAt(0));
          await aui.composer.addAttachment(new File([bytes],'粘贴的图片.png',{type:'image/png'}));
        } else if (payload.text) {
          const input = document.querySelector<HTMLTextAreaElement>('.input');
          if (!input) return;
          const start=input.selectionStart, end=input.selectionEnd;
          aui.composer.setText(input.value.slice(0,start)+payload.text+input.value.slice(end));
          const cursor=start+payload.text.length;
          requestAnimationFrame(()=>{input.setSelectionRange(cursor,cursor);});
        }
      } catch {setPasteError('粘贴失败，请重新复制图片后再试。');}
    };
    window.addEventListener('synor-paste',receive);
    return ()=>window.removeEventListener('synor-paste',receive);
  },[aui]);
  return <ThreadPrimitive.Root className="chat">
    <ThreadPrimitive.Viewport className="viewport"><ThreadPrimitive.Messages components={{UserMessage,AssistantMessage}}/><ThreadPrimitive.ScrollToBottom className="bottom">↓ 回到最新</ThreadPrimitive.ScrollToBottom></ThreadPrimitive.Viewport>
    <ComposerPrimitive.Root className="composer"><div className="attachments"><ComposerPrimitive.Attachments components={{Attachment:ImageAttachment}}/></div><ComposerPrimitive.Input onKeyDown={event=>{
      if (event.metaKey && !event.altKey && !event.shiftKey && event.key.toLowerCase()==='v' && window.webkit) {
        event.preventDefault();
        window.webkit.messageHandlers.synor.postMessage({type:'paste'});
      }
    }} addAttachmentOnPaste className="input" placeholder="输入消息，或粘贴图片…" rows={2} submitMode="enter"/><div className="composer-options"><button type="button" className="thinking-option" role="switch" aria-checked={thinking} disabled={running || !config?.ReasoningControl} title={!config?.ReasoningControl ? '思考行为由服务端决定' : undefined} onClick={()=>aui.composer.setRunConfig({custom:{thinking:!thinking}})}><span className="thinking-indicator" aria-hidden="true"/>思考模式<span>{!config?.ReasoningControl ? '服务端决定' : thinking ? '已开启' : '已关闭'}</span></button><button type="button" className="translation-entry" title="清空当前聊天上下文（⌘L）" aria-keyshortcuts="Meta+L" onClick={()=>window.dispatchEvent(new Event('synor-clear-context'))}><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="m15 9 5-6M13 7l4 4-7 10H3v-7Z"/><path d="m6 14 5 5M3 21h17"/></svg>清理</button><button type="button" className="translation-entry" onClick={()=>window.webkit?.messageHandlers.synor.postMessage({type:'open-translation'})}><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M3 5h12M9 3v2M12 5c-1 6-4 9-9 11M5 8c1 3 4 6 7 8m1 5 4-11 4 11m-6.5-4h5"/></svg>翻译</button><Settings onLoaded={setConfig}/></div><div className="compose-actions"><ThreadPrimitive.If running={false}><ComposerPrimitive.Send className="send" aria-label="发送消息">↑</ComposerPrimitive.Send></ThreadPrimitive.If><ThreadPrimitive.If running><ComposerPrimitive.Cancel className="send stop" aria-label="停止生成">■</ComposerPrimitive.Cancel></ThreadPrimitive.If></div></ComposerPrimitive.Root>
    <footer><span className="keyboard-hint">Enter 发送 · Shift + Enter 换行</span><span className="demo-status" role={pasteError ? "alert" : undefined}>{pasteError || <><span className="dot"/>{config?.Model ?? '读取配置中…'}</>}</span></footer>
  </ThreadPrimitive.Root>;
}
function App() {
  useEffect(()=>{window.webkit?.messageHandlers.synor.postMessage({type:'ready'});},[]);
  const runtime = useLocalRuntime(adapter,{adapters:{attachments:new SimpleImageAttachmentAdapter()}});
  return <AssistantRuntimeProvider runtime={runtime}><Chat/></AssistantRuntimeProvider>;
}
createRoot(document.getElementById('root')!).render(window.__SYNOR_TRANSLATION__ ? <Translation/> : <App/>);
window.addEventListener('keydown', event=>{if(event.key==='Escape' && !event.isComposing && !document.querySelector('dialog[open]')) window.webkit?.messageHandlers.synor.postMessage({type:'close'});});
