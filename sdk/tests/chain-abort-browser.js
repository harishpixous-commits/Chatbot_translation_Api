const { chromium } = require("playwright-core");
(async()=>{
 const b=await chromium.launch({executablePath:process.env.CHROME_EXE,args:["--no-sandbox","--disable-dev-shm-usage"]});
 const p=await b.newPage(); await p.goto(process.env.BASE+"/demo.html",{waitUntil:"load"}); await p.waitForTimeout(1500);
 const call=(t,ms)=>p.evaluate(async([t,ms])=>{const c=new AbortController();const to=ms&&setTimeout(()=>c.abort(),ms);const t0=performance.now();try{const r=await fetch("/api/ai/translation/batch",{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({texts:[t],sourceLanguage:"en",targetLanguage:"ta"}),signal:c.signal});return [r.status,(performance.now()-t0)/1000]}catch(e){return [e.name,(performance.now()-t0)/1000]}},[t,ms]);
 console.log("warm",JSON.stringify(await call("warm",0)));
 console.log("abort dense",JSON.stringify(await call(("a b ".repeat(500)).slice(0,2000),3000)));
 console.log("next",JSON.stringify(await call("Good morning",0)));
 const h=await (await fetch("http://127.0.0.1:8765/health")).json(); console.log("worker",JSON.stringify(h.worker));
 await b.close();
})().catch(e=>{console.log("ERR",e.message);process.exit(1)});
