import {useEffect,useRef,useState} from 'react';
import {createPortal} from 'react-dom';

type Config = {BaseUrl:string;Model:string;ApiKey:string;SystemPrompt:string;ReasoningControl:boolean;WorkingDirectory:string;ApiMode:'Responses'|'ChatCompletions';UserAgent:string};
export function Settings({onLoaded}:{onLoaded:(settings:Config)=>void}) {
  const dialog=useRef<HTMLDialogElement>(null);
  const requestId=useRef('');
  const saved=useRef<Config | undefined>(undefined);
  const [form,setForm]=useState<Config>({BaseUrl:'',Model:'',ApiKey:'',SystemPrompt:'',ReasoningControl:true,WorkingDirectory:'',ApiMode:'Responses',UserAgent:''});
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState('');
  const saving=useRef(false);
  useEffect(()=>{
    const receive=(event:Event)=>{
      const result=(event as CustomEvent).detail;
      if(result.id!==requestId.current)return;
      setBusy(false);
      if(result.error){setError(result.error);return;}
      if(result.settings){
        saved.current=result.settings;setForm(result.settings);onLoaded(result.settings);
        if(saving.current)dialog.current?.close();
      }
    };
    window.addEventListener('synor-chat',receive);
    requestId.current=crypto.randomUUID();setBusy(true);
    window.webkit?.messageHandlers.synor.postMessage({type:'settings-load',id:requestId.current});
    return ()=>window.removeEventListener('synor-chat',receive);
  },[onLoaded]);
  return <><button type="button" className="settings-entry" onClick={()=>{
    if(saved.current){setForm(saved.current);setError('');}
    dialog.current?.showModal();
  }}>配置</button>{createPortal(<dialog className="settings-panel" ref={dialog} aria-labelledby="settings-title">
    <form onSubmit={event=>{
      event.preventDefault();event.stopPropagation();setError('');setBusy(true);saving.current=true;
      requestId.current=crypto.randomUUID();
      window.webkit?.messageHandlers.synor.postMessage({type:'settings-save',id:requestId.current,settings:form});
    }}>
      <div className="settings-heading"><h2 id="settings-title">AI 配置</h2><button type="button" onClick={()=>dialog.current?.close()}>返回聊天</button></div>
      <p className="settings-description">连接 OpenAI 兼容服务，保存后对下一次提问生效。</p>
      <label>API 模式<select value={form.ApiMode} onChange={e=>setForm({...form,ApiMode:e.target.value as Config['ApiMode']})}><option value="Responses">OpenAI Responses</option><option value="ChatCompletions">Chat Completions</option></select><small>LM Studio 和 CLIProxyAPI 可按服务端支持选择协议。</small></label>
      <label>API 基础地址<input required type="url" value={form.BaseUrl} placeholder="http://127.0.0.1:1234/v1" onChange={e=>setForm({...form,BaseUrl:e.target.value})}/><small>包含 /v1 等版本路径，不包含具体接口路径；远程服务使用 HTTPS。</small></label>
      <label>模型名称<input required value={form.Model} placeholder="google/gemma-4-e4b" onChange={e=>setForm({...form,Model:e.target.value})}/></label>
      <label>API Key<input type="password" autoComplete="off" value={form.ApiKey} placeholder="本地无认证服务可留空" onChange={e=>setForm({...form,ApiKey:e.target.value})}/><small>保存在 macOS 钥匙串中；清空并保存可移除密钥。</small></label>
      <label>User-Agent<input value={form.UserAgent} placeholder="pi (macOS 26.6.1; arm64)" onChange={e=>setForm({...form,UserAgent:e.target.value})}/><small>部分中转服务（如 agentrouter）只放行已知客户端标识；留空使用与 Pi 一致的默认值。</small></label>
      <label>工作目录<input required value={form.WorkingDirectory} placeholder="例如 ~/Desktop/MyProject" onChange={e=>setForm({...form,WorkingDirectory:e.target.value})}/><small>工具以当前用户权限自动执行，此目录用于解析相对路径，不限制访问范围。</small></label>
      <label>附加系统提示词<textarea rows={5} value={form.SystemPrompt} placeholder="默认使用 Pi 风格工具提示词。这里可追加项目要求，例如：请用中文回答。" onChange={e=>setForm({...form,SystemPrompt:e.target.value})}/></label>
      <label className="settings-check"><input type="checkbox" checked={form.ReasoningControl} onChange={e=>setForm({...form,ReasoningControl:e.target.checked})}/>发送思考控制参数</label>
      <p className="settings-description">服务不支持 reasoning_effort 时关闭此项，思考行为将由服务端决定。</p>
      {error && <p role="alert" className="settings-error">{error}</p>}
      <div className="settings-actions"><button type="button" onClick={()=>dialog.current?.close()}>取消</button><button className="settings-save" type="submit" disabled={busy}>{busy?'处理中…':'保存配置'}</button></div>
    </form>
  </dialog>,document.body)}</>;
}
