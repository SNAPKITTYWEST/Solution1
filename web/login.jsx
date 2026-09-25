import React,{useEffect,useState} from 'react';
import {createRoot} from 'react-dom/client';
import Button from '@cloudscape-design/components/button';
import Input from '@cloudscape-design/components/input';
import Alert from '@cloudscape-design/components/alert';
import Spinner from '@cloudscape-design/components/spinner';
import {applyMode,Mode} from '@cloudscape-design/global-styles';
import '@cloudscape-design/global-styles/index.css';
import './styles.css';

// Sign-in page. The SAML handshake finishes on the .NET service, which redirects back here with
// a short-lived JWT in the URL fragment. The fragment is read once, cleared from the address bar,
// and handed to the playground through sessionStorage so the credential never leaves the tab.
const SERVICE=(sessionStorage.getItem('sovereign-endpoint')||'http://127.0.0.1:5080').replace(/\/+$/,'');

function Login(){
 const [endpoint,setEndpoint]=useState(SERVICE),[error,setError]=useState(''),[busy,setBusy]=useState(false);
 const [user,setUser]=useState('admin'),[secret,setSecret]=useState(''),[methods,setMethods]=useState(null);
 useEffect(()=>{document.documentElement.dataset.theme='dark';applyMode(Mode.Dark);},[]);
 useEffect(()=>{
  const fragment=new URLSearchParams(window.location.hash.replace(/^#/,''));
  const token=fragment.get('token');
  if(!token)return;
  // Clear the fragment at once so the credential is not left sitting in the address bar.
  history.replaceState(null,'',window.location.pathname);
  adopt(token,SERVICE);
 },[]);

 async function discover(){
  const base=endpoint.replace(/\/+$/,'').replace(/\/login$/,'');
  try{setMethods(await (await fetch(`${base}/auth/status`,{signal:AbortSignal.timeout(10000)})).json());}
  catch{setMethods({passwordEnabled:false,samlEnabled:false});}
 }
 useEffect(()=>{discover();},[]);

 async function adopt(token,base){
  try{
   const response=await fetch(`${base}/api/status`,{headers:{Authorization:`Bearer ${token}`},signal:AbortSignal.timeout(15000)});
   if(!response.ok)throw Error(`The service rejected this session (HTTP ${response.status}).`);
   sessionStorage.setItem('sovereign-token',token);
   sessionStorage.setItem('sovereign-endpoint',base);
   window.location.replace('./');
  }catch(e){sessionStorage.removeItem('sovereign-token');setError(e.message);}
 }
 async function begin(event){
  event.preventDefault();
  setBusy(true);setError('');
  const base=endpoint.replace(/\/+$/,'').replace(/\/login$/,'');
  sessionStorage.setItem('sovereign-endpoint',base);
  try{
   if(methods?.passwordEnabled){
    const body=new URLSearchParams({username:user,password:secret});
    const response=await fetch(`${base}/auth/login`,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body,signal:AbortSignal.timeout(20000)});
    const data=await response.json().catch(()=>({}));
    if(!response.ok)throw Error(data.error||`Sign-in failed (HTTP ${response.status}).`);
    setSecret('');
    await adopt(data.token,base);
    return;
   }
   if(!methods?.samlEnabled)throw Error(`No sign-in method is configured on the service at ${base}.`);
   // The service issues a one-time RelayState nonce and redirects to the identity provider.
   window.location.href=`${base}/saml/login`;
  }catch(e){setError(e.message);setBusy(false);}
 }
 const password=methods?.passwordEnabled,saml=methods?.samlEnabled;
 return <div className="login-page">
  <section className="login-card">
   <a className="brand login-brand" href="./"><span className="brand-mark">S</span><span>SOVEREIGN<small>COMPUTE, ON YOUR TERMS</small></span></a>
   <h1>Sign in</h1>
   <p className="muted">{password&&!saml?'Enter your administrator password.':saml&&!password?'Continue with your organisation’s identity provider.':'Choose how to sign in. Sovereign never stores your password.'}</p>
   {error&&<div className="alert"><Alert type="error" dismissible onDismiss={()=>setError('')}>{error}</Alert></div>}
   <form onSubmit={begin}>
    <label htmlFor="endpoint">Sovereign service</label>
    <Input ariaLabel="Sovereign service URL" id="endpoint" value={endpoint} onChange={({detail})=>{setEndpoint(detail.value);setMethods(null);}} placeholder="http://127.0.0.1:5080"/>
    {password&&<>
     <label htmlFor="username">Username</label>
     <Input ariaLabel="Username" id="username" value={user} onChange={({detail})=>setUser(detail.value)} autoComplete="username"/>
     <label htmlFor="password">Password</label>
     <Input ariaLabel="Password" id="password" type="password" value={secret} onChange={({detail})=>setSecret(detail.value)} autoComplete="current-password"/>
    </>}
    {methods&&!password&&!saml&&<p className="login-note">This service exposes no sign-in method. Ask its operator to configure SAML or <code>SOVEREIGN_ADMIN_PASSWORD_HASH</code>.</p>}
    <Button variant="primary" type="submit" loading={busy} disabled={busy||!methods}>{saml&&!password?'Sign in with SAML':password?'Sign in':'Sign in'}</Button>
   </form>
   {busy&&saml&&!password&&<div className="login-redirect"><Spinner size="small"/><span>Redirecting to your identity provider…</span></div>}
   <footer className="login-foot">
    <a href="https://github.com/SNAPKITTYWEST/Solution1" target="_blank" rel="noreferrer">Repository</a>
    <a href="./">Continue without signing in</a>
   </footer>
  </section>
 </div>
}
createRoot(document.getElementById('root')).render(<Login/>);
